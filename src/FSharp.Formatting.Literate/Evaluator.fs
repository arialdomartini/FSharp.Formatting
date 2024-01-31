namespace FSharp.Formatting.Literate.Evaluation

open System
open System.IO
open FSharp.Formatting.Markdown
open FSharp.Formatting.Internal

// ------------------------------------------------------------------------------------------------
// Evaluator
// ------------------------------------------------------------------------------------------------

/// <summary>
///   Represents a kind of thing that can be embedded
/// </summary>
/// <namespacedoc>
///   <summary>Functionality to support literate evaluation for F# scripts</summary>
/// </namespacedoc>
[<RequireQualifiedAccessAttribute>]
type FsiEmbedKind =
    /// The FSI output
    | FsiOutput
    /// The combined FSI output and console output
    | FsiMergedOutput
    /// The stdout from this part of the execution (not including FSI output)
    | ConsoleOutput
    /// The 'it' value
    | ItValue
    /// The 'it' value as raw text
    | ItRaw
    /// A specific value
    | Value

/// An interface that represents FSI evaluation result
/// (we make this abstract so that evaluators can store other info)
type IFsiEvaluationResult = interface end

/// Represents the result of evaluating an F# snippet. This contains
/// the generated console output together with a result and its static type.
type FsiEvaluationResult =
    { Output: string option
      FsiOutput: string option
      FsiMergedOutput: string option
      ItValue: (obj * Type) option
      Result: (obj * Type) option }

    interface IFsiEvaluationResult

/// Record that is reported by the EvaluationFailed event when something
/// goes wrong during evalutaiton of an expression
type FsiEvaluationFailedInfo =
    { Text: string
      AsExpression: bool
      File: string option
      Exception: exn
      StdErr: string }

    override x.ToString() =
        let indent (s: string) =
            s.Split([| '\n'; '\r' |], StringSplitOptions.RemoveEmptyEntries)
            |> Array.map (fun x -> "    " + x)
            |> fun x -> String.Join("\n", x)

        sprintf "Error evaluating expression \nExpression:\n%s\nError:\n%s" (indent x.Text) (indent x.StdErr)

/// Represents an evaluator for F# snippets embedded in code
type IFsiEvaluator =

    /// Called to format some part of evaluation result generated by FSI
    abstract Format: result: IFsiEvaluationResult * kind: FsiEmbedKind * executionCount: int -> MarkdownParagraphs

    /// Called to evaluate a snippet
    abstract Evaluate: code: string * asExpression: bool * file: string option -> IFsiEvaluationResult

/// Represents a simple (fake) event loop for the 'fsi' object
type private NoOpFsiEventLoop() =
    member x.Run() = ()
    member x.Invoke<'T>(f: unit -> 'T) = f ()
    member x.ScheduleRestart() = ()

/// Implements a simple 'fsi' object to be passed to the FSI evaluator
[<Sealed>]
type private NoOpFsiObject() =
    let mutable evLoop = new NoOpFsiEventLoop()
    let mutable showIDictionary = true
    let mutable showDeclarationValues = true
    let mutable args = Environment.GetCommandLineArgs()
    let mutable fpfmt = "g10"

    let mutable fp = (System.Globalization.CultureInfo.InvariantCulture :> System.IFormatProvider)

    let mutable printWidth = 78
    let mutable printDepth = 100
    let mutable printLength = 100
    let mutable printSize = 10000
    let mutable showIEnumerable = true
    let mutable showProperties = true
    let mutable addedPrinters: Choice<System.Type * (obj -> string), System.Type * (obj -> obj)> list = []

    member self.FloatingPointFormat
        with get () = fpfmt
        and set v = fpfmt <- v

    member self.FormatProvider
        with get () = fp
        and set v = fp <- v

    member self.PrintWidth
        with get () = printWidth
        and set v = printWidth <- v

    member self.PrintDepth
        with get () = printDepth
        and set v = printDepth <- v

    member self.PrintLength
        with get () = printLength
        and set v = printLength <- v

    member self.PrintSize
        with get () = printSize
        and set v = printSize <- v

    member self.ShowDeclarationValues
        with get () = showDeclarationValues
        and set v = showDeclarationValues <- v

    member self.ShowProperties
        with get () = showProperties
        and set v = showProperties <- v

    member self.ShowIEnumerable
        with get () = showIEnumerable
        and set v = showIEnumerable <- v

    member self.ShowIDictionary
        with get () = showIDictionary
        and set v = showIDictionary <- v

    member self.AddedPrinters
        with get () = addedPrinters
        and set v = addedPrinters <- v

    member self.CommandLineArgs
        with get () = args
        and set v = args <- v

    member self.AddPrinter(_printer: 'T -> string) = ()

    member self.EventLoop
        with get () = evLoop
        and set (_x: NoOpFsiEventLoop) = ()

    member self.AddPrintTransformer(_printer: 'T -> obj) = ()

/// Provides configuration options for the FsiEvaluator
type FsiEvaluatorConfig() =
    /// Creates a dummy fsi object that does not affect the behaviour of F# Interactive
    /// (and simply ignores all operations that are done on it). You can use this to
    /// e.g. disable registered printers that would open new windows etc.
    static member CreateNoOpFsiObject() = box (new NoOpFsiObject())

/// A wrapper for F# interactive service that is used to evaluate inline snippets
type FsiEvaluator
    (
        ?options: string array,
        ?fsiObj: obj,
        ?addHtmlPrinter: bool,
        ?discardStdOut: bool,
        ?disableFsiObj: bool,
        ?onError: (string -> unit)
    ) =

    let discardStdOut = defaultArg discardStdOut true

    let fsiObj = defaultArg fsiObj (box FSharp.Compiler.Interactive.Shell.Settings.fsi)

    let addHtmlPrinter = defaultArg addHtmlPrinter true
    let disableFsiObj = defaultArg disableFsiObj false
    let onError = defaultArg onError ignore

    let fsiOptions =
        options
        |> Option.map FsiOptions.ofArgs
        |> Option.defaultWith (fun _ -> FsiOptions.Default)

    let fsiOptions =
        if addHtmlPrinter then
            { fsiOptions with
                Defines = fsiOptions.Defines @ [ "HAS_FSI_ADDHTMLPRINTER" ] }
        else
            fsiOptions

    let fsiSession = ScriptHost.Create(fsiOptions, discardStdOut = discardStdOut, fsiObj = fsiObj)

    let mutable plainTextPrinters: Choice<(obj -> string option), (obj -> obj option)> list = []
    let mutable htmlPrinters: Choice<(obj -> ((string * string) seq * string) option), (obj -> obj option)> list = []

    //----------------------------------------------------
    // Inject the standard 'fsi' script control model into the evaluation session
    // without referencing FSharp.Compiler.Interactive.Settings (which is highly problematic)
    //
    // Injecting arbitrary .NET values into F# interactive sessions from the outside is non-trivial.
    // The technique here is to inject a script which reads values out of static fields
    // in this assembly via reflection.

    let thisTypeName = typeof<FsiEvaluator>.AssemblyQualifiedName

    let addPrinterThunk (f: obj, ty: Type) =
        let realPrinter (value: obj) =
            match value with
            | null -> None
            | _ ->
                if ty.IsAssignableFrom(value.GetType()) then
                    match f with
                    | :? (obj -> string) as f2 ->
                        match f2 value with
                        | null -> None
                        | s -> Some s
                    | _ -> None
                else
                    None

        plainTextPrinters <- Choice1Of2 realPrinter :: plainTextPrinters

    let addPrintTransformerThunk (f: obj, ty: Type) =
        let realPrinter (value: obj) =
            match value with
            | null -> None
            | _ ->
                if ty.IsAssignableFrom(value.GetType()) then
                    match f with
                    | :? (obj -> obj) as f2 ->
                        match f2 value with
                        | null -> None
                        | o -> Some o
                    | _ -> None
                else
                    None

        plainTextPrinters <- Choice2Of2 realPrinter :: plainTextPrinters
        htmlPrinters <- Choice2Of2 realPrinter :: htmlPrinters

    let addHtmlPrinterThunk (f: obj, ty: Type) =
        let realHtmlPrinter (value: obj) =
            match value with
            | null -> None
            | _ ->
                if ty.IsAssignableFrom(value.GetType()) then
                    match f with
                    | :? (obj -> (string * string) seq * string) as f2 -> Some(f2 value)
                    | _ -> None
                else
                    None

        htmlPrinters <- Choice1Of2 realHtmlPrinter :: htmlPrinters

    let fsiEstablishText =
        sprintf
            """
namespace global
[<AutoOpen>]
module __FsiSettings =
   open System
   open System.Reflection
   type fsi private() =
       static let ty = System.Type.GetType("%s")
       static let __InjectedAddPrinter =
           ty.InvokeMember("InjectedAddPrinter", (BindingFlags.Static ||| BindingFlags.Public ||| BindingFlags.NonPublic ||| BindingFlags.GetProperty), null, null, [| |])
           :?> ((obj -> string) * Type -> unit)
       static let __InjectedAddPrintTransformer =
           ty.InvokeMember("InjectedAddPrintTransformer", (BindingFlags.Static ||| BindingFlags.Public ||| BindingFlags.NonPublic ||| BindingFlags.GetProperty), null, null, [| |])
           :?> ((obj -> obj) * Type -> unit)
       static let __InjectedAddHtmlPrinter =
           ty.InvokeMember("InjectedAddHtmlPrinter", (BindingFlags.Static ||| BindingFlags.Public ||| BindingFlags.NonPublic ||| BindingFlags.GetProperty), null, null, [| |])
           :?> ((obj -> seq<string * string> * string) * Type -> unit)
       static let __FsiObj =
          ty.InvokeMember("InjectedFsiObj", (BindingFlags.Static ||| BindingFlags.GetProperty ||| BindingFlags.Public ||| BindingFlags.NonPublic), null, null, [| |])

       static let getInstanceProp nm = __FsiObj.GetType().InvokeMember(nm, (BindingFlags.Instance ||| BindingFlags.GetProperty ||| BindingFlags.Public), null, __FsiObj, [|  |])
       static let setInstanceProp nm v = __FsiObj.GetType().InvokeMember(nm, (BindingFlags.Instance ||| BindingFlags.SetProperty ||| BindingFlags.Public), null, __FsiObj, [| box v |]) |> ignore

       /// Get or set the floating point format used in the output of the interactive session.
       static member FloatingPointFormat
           with get() = getInstanceProp "FloatingPointFormat" :?> string
           and set(v:string) = setInstanceProp "FloatingPointFormat" v

       /// Get or set the format provider used in the output of the interactive session.
       static member FormatProvider
           with get() = getInstanceProp "FormatProvider" :?> System.IFormatProvider
           and set(v:System.IFormatProvider) = setInstanceProp "FormatProvider" v

       /// Get or set the print width of the interactive session.
       static member PrintWidth
           with get() = getInstanceProp "PrintWidth" :?> int
           and set(v:int  ) = setInstanceProp "PrintWidth" v

       /// Get or set the print depth of the interactive session.
       static member PrintDepth
           with get() = getInstanceProp "PrintDepth" :?> int
           and set(v:int) = setInstanceProp "PrintDepth" v

       /// Get or set the total print length of the interactive session.
       static member PrintLength
           with get() = getInstanceProp "PrintLength" :?> int
           and set(v:int) = setInstanceProp "PrintLength" v

       /// Get or set the total print size of the interactive session.
       static member PrintSize
           with get() = getInstanceProp "PrintSize" :?> int
           and set(v:int) = setInstanceProp "PrintSize" v

       /// When set to 'false', disables the display of properties of evaluated objects in the output of the interactive session.
       static member ShowProperties
           with get() = getInstanceProp "ShowProperties" :?> bool
           and set(v:bool) = setInstanceProp "ShowProperties" v

       /// When set to 'false', disables the display of sequences in the output of the interactive session.
       static member ShowIEnumerable
           with get() = getInstanceProp "ShowIEnumerable" :?> bool
           and set(v:bool) = setInstanceProp "ShowIEnumerable" v

       /// When set to 'false', disables the display of declaration values in the output of the interactive session.
       static member ShowDeclarationValues
           with get() = getInstanceProp "ShowDeclarationValues" :?> bool
           and set(v:bool) = setInstanceProp "ShowDeclarationValues" v

       /// Register a printer that controls the output of the interactive session.
       static member AddPrinter<'T>(printer:'T -> string) =
           __InjectedAddPrinter ((fun (v:obj) -> printer (unbox<'T> v)), typeof<'T>)
           __FsiObj.GetType().GetMethod("AddPrinter", (BindingFlags.Instance ||| BindingFlags.Public)).MakeGenericMethod([| typeof<'T> |]).Invoke(__FsiObj, [|  box printer |]) |> ignore

       /// Register a print transformer that controls the output of the interactive session.
       static member AddPrintTransformer<'T>(printer: 'T -> obj) =
           __InjectedAddPrintTransformer ((fun (v:obj) -> printer (unbox<'T> v)), typeof<'T>)
           __FsiObj.GetType().GetMethod("AddPrintTransformer", (BindingFlags.Instance ||| BindingFlags.Public)).MakeGenericMethod([| typeof<'T> |]).Invoke(__FsiObj, [|  box printer |]) |> ignore

       static member AddHtmlPrinter<'T>(printer:'T -> seq<string * string> * string) =
           __InjectedAddHtmlPrinter ((fun (v:obj) -> printer (unbox<'T> v)), typeof<'T>)

       /// The command line arguments after ignoring the arguments relevant to the interactive environment
       static member CommandLineArgs
           with get() = getInstanceProp "CommandLineArgs" :?> string []
           and set(v:string[]) = setInstanceProp "CommandLineArgs" v
    """
            thisTypeName

    do
        if not disableFsiObj then
            FsiEvaluator.InjectedAddPrinter <- addPrinterThunk
            FsiEvaluator.InjectedAddPrintTransformer <- addPrintTransformerThunk
            FsiEvaluator.InjectedAddHtmlPrinter <- addHtmlPrinterThunk
            FsiEvaluator.InjectedFsiObj <- fsiObj

            let path = Path.GetTempFileName() + ".fs"
            File.WriteAllText(path, fsiEstablishText)

            let outputs, res = fsiSession.EvalInteraction(sprintf "#load @\"%s\"" path)

            File.Delete(path)

            match res with
            | Ok _v -> ()
            | Error exn ->
                printfn "Error establishing FSI:"
                printfn "%s" outputs.Output.FsiOutput
                printfn "%s" outputs.Output.ScriptOutput
                printfn "%s" outputs.Error.FsiOutput
                printfn "%s" outputs.Error.ScriptOutput
                printfn "Exception: %A" exn
                raise exn


    let evalFailed = new Event<_>()
    let lockObj = obj ()

    let rec plainTextPrint depth (v: obj) =
        // guard against recursion in print transformers
        if depth > 20 then
            try
                sprintf "%A" v
            with e ->
                e.ToString()
        else
            plainTextPrinters
            // Try to find a printer or print transformer
            |> List.tryPick (fun f ->
                match f with
                | Choice1Of2 addedPrinter ->
                    try
                        addedPrinter v
                    with _ ->
                        None
                | Choice2Of2 addedPrintTransformer ->
                    (try
                        addedPrintTransformer v
                     with _ ->
                         None)
                    |> Option.map (plainTextPrint (depth + 1)))
            |> function
                | None ->
                    // no printer found
                    try
                        sprintf "%A" v
                    with e ->
                        e.ToString()
                | Some t -> t

    let rec tryHtmlPrint depth (v: obj) =
        if depth > 10 then
            None
        else
            htmlPrinters
            // Try to find a printer or print transformer
            |> List.tryPick (fun f ->
                match f with
                | Choice1Of2 addedPrinter ->
                    try
                        addedPrinter v
                    with _ ->
                        None
                | Choice2Of2 addedPrintTransformer ->
                    (try
                        addedPrintTransformer v
                     with _ ->
                         None)
                    |> Option.bind (tryHtmlPrint (depth + 1)))

    /// Registered transformations for pretty printing values
    /// (the default formats value as a string and emits single CodeBlock)
    let mutable valueTransformations: ((obj * Type * int) -> MarkdownParagraph list option) list =
        [ (fun (o: obj, _t: Type, executionCount: int) ->
              tryHtmlPrint 0 o
              |> Option.map (fun (_tags, html) -> [ OutputBlock(html, "text/html", Some executionCount) ]))

          (fun (o: obj, _t: Type, executionCount: int) ->
              Some([ OutputBlock(plainTextPrint 0 o, "text/plain", Some executionCount) ])) ]

    /// Temporarily holds the function value injected into the F# evaluation session
    static member val internal InjectedAddPrintTransformer: ((obj -> obj) * Type -> unit) =
        Unchecked.defaultof<_> with get, set

    /// Temporarily holds the function value injected into the F# evaluation session
    static member val internal InjectedAddPrinter: ((obj -> string) * Type -> unit) =
        Unchecked.defaultof<_> with get, set

    /// Temporarily holds the function value injected into the F# evaluation session
    static member val internal InjectedAddHtmlPrinter: ((obj -> (string * string) seq * string) * Type -> unit) =
        Unchecked.defaultof<_> with get, set

    /// Temporarily holds the object value injected into the F# evaluation session
    static member val internal InjectedFsiObj: obj = Unchecked.defaultof<_> with get, set

    /// Register a function that formats (some) values that are produced by the evaluator.
    /// The specified function should return 'Some' when it knows how to format a value
    /// and it should return formatted
    member x.RegisterTransformation(f) =
        valueTransformations <- f :: valueTransformations

    /// This event is fired whenever an evaluation of an expression fails
    member x.EvaluationFailed = evalFailed.Publish

    interface IFsiEvaluator with
        /// Format a specified result or value
        member x.Format(result, kind, executionCount) =
            if not (result :? FsiEvaluationResult) then
                invalidArg "result" "FsiEvaluator.Format: Expected 'FsiEvaluationResult' value as argument."

            match result :?> FsiEvaluationResult, kind with
            | result, FsiEmbedKind.ConsoleOutput ->
                let outputText = defaultArg result.Output "No output has been produced."

                let output = outputText.Trim()
                [ OutputBlock(output, "text/plain", Some executionCount) ]
            | result, FsiEmbedKind.FsiOutput ->
                let outputText = defaultArg result.FsiOutput "No output has been produced."

                let output = outputText.Trim()
                [ OutputBlock(output, "text/plain", Some executionCount) ]
            | result, FsiEmbedKind.FsiMergedOutput ->
                let outputText = defaultArg result.FsiMergedOutput "No output has been produced."

                let output = outputText.Trim()
                [ OutputBlock(output, "text/plain", Some executionCount) ]
            | { ItValue = Some(obj, ty) }, FsiEmbedKind.ItRaw ->
                match
                    valueTransformations
                    |> List.pick (fun f -> lock lockObj (fun () -> f (obj, ty, executionCount)))
                with
                | [] -> [ OutputBlock("No value returned by any evaluator", "text/plain", Some executionCount) ]
                | blocks ->
                    blocks
                    |> List.map (function
                        | OutputBlock(output, _, Some executionCount) ->
                            let output =
                                if ty.FullName = (typeof<string>).FullName then
                                    let l = output.Length
                                    output.Substring(1, l - 2)
                                else
                                    output

                            OutputBlock(output, "text/html", Some executionCount)
                        | _ -> OutputBlock("Value could not be returned raw", "text/plain", Some executionCount))
            | { ItValue = Some(obj, ty) }, FsiEmbedKind.ItValue
            | { Result = Some(obj, ty) }, FsiEmbedKind.Value ->
                match
                    valueTransformations
                    |> List.pick (fun f -> lock lockObj (fun () -> f (obj, ty, executionCount)))
                with
                | [] -> [ OutputBlock("No value returned by any evaluator", "text/plain", Some executionCount) ]
                | blocks -> blocks

            | _ -> [ OutputBlock("No value returned by any evaluator", "text/plain", Some executionCount) ]

        /// Evaluates the given text in an fsi session and returns
        /// an FsiEvaluationResult.
        ///
        /// If evaluated as an expression, Result should be set with the
        /// result of evaluating the text as an F# expression.
        /// If not, just the console output of the evaluation is captured and
        /// returned in Output.
        ///
        /// If file is set, the text will be evaluated as if it was present in the
        /// given script file - this is for correct usage of #I and #r with relative paths.
        /// Note however that __SOURCE_DIRECTORY___ does not currently pick this up.
        member x.Evaluate(text: string, asExpression, ?file) : IFsiEvaluationResult =
            try
                lock lockObj
                <| fun () ->
                    let dir =
                        match file with
                        | Some f -> Path.GetDirectoryName f
                        | None -> Directory.GetCurrentDirectory()

                    fsiSession.WithCurrentDirectory dir (fun () ->
                        let output, value, itvalue =
                            if asExpression then
                                let output, res = fsiSession.TryEvalExpression text

                                match res with
                                | Error _ -> output, None, None
                                | Ok res -> output, res, None
                            else
                                let output, res = fsiSession.EvalInteraction text

                                match res with
                                | Error _ -> output, None, None
                                | Ok _ ->
                                    // try get the "it" value, but silently ignore any errors
                                    let _outputs, res = fsiSession.TryEvalExpression "it"

                                    match res with
                                    | Ok v -> output, None, v
                                    | Error _ -> output, None, None

                        { Output = Some output.Output.ScriptOutput
                          FsiMergedOutput = Some output.Output.Merged
                          FsiOutput = Some output.Output.FsiOutput
                          Result = value
                          ItValue = itvalue }
                        :> IFsiEvaluationResult)
            with :? FsiEvaluationException as e ->
                evalFailed.Trigger
                    { File = file
                      AsExpression = asExpression
                      Text = text
                      Exception = e
                      StdErr = e.Result.Error.Merged }

                let msg =
                    $"Evaluation failed and --strict is on\n    file=%A{file}\n    asExpression=%b{asExpression}, text=%s{text}\n    stdout=%s{e.Result.Output.Merged}\n\    stderr=%s{e.Result.Error.Merged}\n    inner exception=%A{e.InnerException}"

                onError msg

                { Output = None
                  FsiOutput = None
                  FsiMergedOutput = None
                  Result = None
                  ItValue = None }
                :> IFsiEvaluationResult
