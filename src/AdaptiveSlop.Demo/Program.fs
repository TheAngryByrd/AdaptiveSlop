open System
open System.Threading
open AdaptiveSlop.Core
open AdaptiveSlop.Tui

let createStatusWidget (title: IAdaptiveValue<string>) (status: IAdaptiveValue<string>) (spinner: Widget) =
    let titleWidget = View.textValue [] title |> Layout.padRight 2 ' '
    let statusWidget = View.textValue [] status
    View.hstack [] " " [ spinner; titleWidget; statusWidget ]

let createLogWidget (lines: IAdaptiveValue<string list>) =
    let lineValues =
        AVal.map (fun entries ->
            entries
            |> List.map (View.text [])
            |> Layout.vstack) lines
    Widget.text (AVal.bind (fun widget -> widget.Content) lineValues)

let mainLoop (framesPerSecond: int) =
    let title = CVal.create "adaptive-sloop"
    let status = CVal.create "booting"
    let logs = CVal.create [ "starting demo" ]
    let ticks = CVal.create 0
    let windowWidth = CVal.create Console.WindowWidth
    let windowHeight = CVal.create Console.WindowHeight

    let statusText =
        AVal.map2 (fun state count -> $"{state} | ticks={count}") (CVal.value status) (CVal.value ticks)

    let spinnerFrames = [ "⠋"; "⠙"; "⠹"; "⠸"; "⠼"; "⠴"; "⠦"; "⠧"; "⠇"; "⠏" ]
    let spinner = View.spinner [] spinnerFrames (CVal.value ticks)
    let header = createStatusWidget (CVal.value title) statusText spinner

    let logWidget =
        AVal.map (fun entries ->
            entries
            |> List.rev
            |> List.truncate 8
            |> List.rev) (CVal.value logs)
        |> createLogWidget

    let progressValue =
        AVal.map (fun count ->
            let cycle = count % 100
            float cycle / 100.0) (CVal.value ticks)

    let progressBar = View.progressBar [] 30 progressValue
    let stats =
        [ View.keyValue [] "status" (CVal.value status)
          View.keyValue [] "ticks" (AVal.map string (CVal.value ticks))
          progressBar ]
        |> View.vstack []
        |> Layout.border '=' '|' 

    let topRow = View.responsiveRow [] (CVal.value windowWidth) 2 header stats
    let body = Layout.vstack [ Layout.border '-' '|' logWidget ]
    let footer = AVal.constant "Press Q to quit"
    let content = Layout.vstack [ topRow; body; Text.ofValue footer ]
    let widthAndContent =
        AVal.map2 (fun (width: int) (widgetText: string) -> width, widgetText) (CVal.value windowWidth) content.Content
    let paddedContent =
        AVal.map2 (fun (height: int) (widthAndText: int * string) ->
            let width, widgetText = widthAndText
            let lines = widgetText.Split([|"\r\n"; "\n"|], StringSplitOptions.None)
            let paddedLines = lines |> Array.map (fun line -> line.PadRight(width))
            if paddedLines.Length >= height then
                paddedLines
                |> Array.take height
                |> String.concat Environment.NewLine
            else
                let blankLine = String(' ', width)
                Array.concat [ paddedLines; Array.create (height - paddedLines.Length) blankLine ]
                |> String.concat Environment.NewLine
        ) (CVal.value windowHeight) widthAndContent

   
    let renderLock = obj()

    let render =
        let mutable lastOutput: string option = None
        fun content ->
            if Monitor.TryEnter(renderLock) then
                try
                    let output : string = AVal.getValue content
                    if lastOutput <> Some output then
                        Console.SetCursorPosition(0, 0)
                        Console.Write(output)
                        lastOutput <- Some output
                finally
                    Monitor.Exit(renderLock)

    let frameDelay = max 1 (1000 / max 1 framesPerSecond)
    let tickDelay = frameDelay / 2 //750
    let mutable running = true

    use _tickTimer = new Timer(TimerCallback(fun _ ->
        try
            let next = (AVal.getValue (CVal.value ticks)) + 1
            ticks.Set(next)
            logs.Set($"tick {next}" :: AVal.getValue (CVal.value logs))
            if next = 1 then status.Set("running")
        with ex -> Console.Error.WriteLine(ex)
    ), null, 0, tickDelay)

    use _renderTimer = new Timer(TimerCallback(fun _ ->
        try
            let width = Console.WindowWidth
            let height = Console.WindowHeight
            if width <> AVal.getValue (CVal.value windowWidth) then
                windowWidth.Set(width)
            if height <> AVal.getValue (CVal.value windowHeight) then
                windowHeight.Set(height)
            render(paddedContent)
        with ex -> Console.Error.WriteLine(ex)
    ), null, 0, frameDelay)

    while running do
        if Console.KeyAvailable then
            let key = Console.ReadKey(true)
            if key.Key = ConsoleKey.Q then
                running <- false
        Thread.Sleep(50)

[<EntryPoint>]
let main args =
    Console.CursorVisible <- false
    try
        let fps =
            match args with
            | [| value |] ->
                match Int32.TryParse(value) with
                | true, parsed when parsed > 0 -> parsed
                | _ -> 30
            | _ -> 30
        mainLoop fps
        0
    finally
        Console.CursorVisible <- true
