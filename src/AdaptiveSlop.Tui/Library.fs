namespace AdaptiveSlop.Tui

open System
open System.Text
open AdaptiveSlop.Core

type Widget =
    { Content: IAdaptiveValue<string> }

module Widget =
    let private combineWith (separator: string) (widgets: Widget list) =
        let contentValues = widgets |> List.map (fun widget -> widget.Content)
        match contentValues with
        | [] -> AVal.constant ""
        | first :: rest ->
            rest
            |> List.fold (fun acc next ->
                AVal.map2 (fun left right -> left + separator + right) acc next
            ) first

    let text (value: IAdaptiveValue<string>) =
        { Content = value }

    let constant (value: string) =
        { Content = AVal.constant value }

    let map (f: string -> string) (widget: Widget) =
        { Content = AVal.map f widget.Content }

    let map2 (f: string -> string -> string) (left: Widget) (right: Widget) =
        { Content = AVal.map2 f left.Content right.Content }

    let vstack (widgets: Widget list) =
        { Content = combineWith Environment.NewLine widgets }

    let hstack (separator: string) (widgets: Widget list) =
        { Content = combineWith separator widgets }

    let padLeft (width: int) (padChar: char) (widget: Widget) =
        map (fun value -> value.PadLeft(width, padChar)) widget

    let padRight (width: int) (padChar: char) (widget: Widget) =
        map (fun value -> value.PadRight(width, padChar)) widget

    let pad (left: int) (right: int) (padChar: char) (widget: Widget) =
        map (fun value ->
            let padded = value.PadLeft(value.Length + left, padChar)
            padded.PadRight(padded.Length + right, padChar)) widget

    let border (horizontal: char) (vertical: char) (widget: Widget) =
        map (fun value ->
            let lines = value.Split([|"\r\n"; "\n"|], StringSplitOptions.None)
            let width = lines |> Seq.fold (fun acc line -> max acc line.Length) 0
            let horizontalLine = String(horizontal, width + 2)
            let builder = StringBuilder()

            builder.Append(horizontalLine) |> ignore
            for line in lines do
                builder.Append(Environment.NewLine) |> ignore
                builder.Append(vertical) |> ignore
                builder.Append(line.PadRight(width)) |> ignore
                builder.Append(vertical) |> ignore
            builder.Append(Environment.NewLine).Append(horizontalLine) |> ignore
            builder.ToString()) widget

    let textbox (width: int) (widget: Widget) =
        map (fun value ->
            if value.Length <= width then
                value.PadRight(width)
            else
                value.Substring(0, width)) widget

    let table (headers: string list) (rows: (string list) list) =
        let allRows = headers :: rows
        let columnCount = allRows |> List.fold (fun acc row -> max acc row.Length) 0
        let paddedRows =
            allRows
            |> List.map (fun row ->
                row
                |> List.append (List.replicate (columnCount - row.Length) ""))

        let columnWidths =
            [0 .. columnCount - 1]
            |> List.map (fun index ->
                paddedRows
                |> List.fold (fun acc row -> max acc row[index].Length) 0)

        let renderRow (row: string list) =
            row
            |> List.mapi (fun index cell -> cell.PadRight(columnWidths[index]))
            |> String.concat " | "

        let headerLine = renderRow paddedRows.Head
        let separator =
            columnWidths
            |> List.map (fun width -> String('-', width))
            |> String.concat "-+-"

        let body = paddedRows.Tail |> List.map renderRow
        let content =
            [ headerLine; separator ]
            |> List.append body
            |> String.concat Environment.NewLine

        { Content = AVal.constant content }

    let progressBar (width: int) (value: IAdaptiveValue<float>) =
        let clamped =
            AVal.map (fun v ->
                if v < 0.0 then 0.0
                elif v > 1.0 then 1.0
                else v) value
        let bar =
            AVal.map (fun v ->
                let filled = int (Math.Round(v * float width))
                let safeFilled = max 0 (min width filled)
                let empty = width - safeFilled
                let fillText = String('█', safeFilled)
                let emptyText = String('░', empty)
                let percent = int (v * 100.0)
                $"[{fillText}{emptyText}] {percent}%%") clamped
        { Content = bar }

    let spinner (frames: string list) (index: IAdaptiveValue<int>) =
        let frameList = if frames.IsEmpty then [ "|" ] else frames
        let content =
            AVal.map (fun value ->
                let idx = abs value % frameList.Length
                frameList[idx]) index
        { Content = content }

    let keyValue (key: string) (value: IAdaptiveValue<string>) =
        map (fun v -> $"{key}: {v}") (text value)

    let responsiveRow (width: IAdaptiveValue<int>) (gap: int) (left: Widget) (right: Widget) =
        let widthAndLeft =
            AVal.map2 (fun w l -> w, l) width left.Content
        let content =
            AVal.map2 (fun (availableWidth: int, leftContent: string) (rightContent: string) ->
                let splitLines (value: string) =
                    value.Split([|"\r\n"; "\n"|], StringSplitOptions.None)
                let leftLines = splitLines leftContent
                let rightLines = splitLines rightContent
                let leftWidth = leftLines |> Seq.fold (fun acc line -> max acc line.Length) 0
                let rightWidth = rightLines |> Seq.fold (fun acc line -> max acc line.Length) 0
                let minGap = max 1 gap
                if leftWidth + rightWidth + minGap <= availableWidth then
                    let totalRows = max leftLines.Length rightLines.Length
                    let spacing = max minGap (availableWidth - leftWidth - rightWidth)
                    let builder = StringBuilder()
                    for row in 0 .. totalRows - 1 do
                        if row > 0 then
                            builder.Append(Environment.NewLine) |> ignore
                        let leftLine = if row < leftLines.Length then leftLines[row] else ""
                        let rightLine = if row < rightLines.Length then rightLines[row] else ""
                        let paddedLeft = leftLine.PadRight(leftWidth)
                        let paddedRight = rightLine.PadRight(rightWidth)
                        builder.Append(paddedLeft) |> ignore
                        builder.Append(String(' ', spacing)) |> ignore
                        builder.Append(paddedRight) |> ignore
                    builder.ToString()
                else
                    leftContent + Environment.NewLine + rightContent
            ) widthAndLeft right.Content
        { Content = content }

    let sidebar (width: IAdaptiveValue<int>) (gap: int) (main: Widget) (side: Widget) =
        let widthAndMain =
            AVal.map2 (fun w m -> w, m) width main.Content
        let content =
            AVal.map2 (fun (availableWidth: int, mainContent: string) (sideContent: string) ->
                let splitLines (value: string) =
                    value.Split([|"\r\n"; "\n"|], StringSplitOptions.None)
                let mainLines = splitLines mainContent
                let sideLines = splitLines sideContent
                let mainWidth = mainLines |> Seq.fold (fun acc line -> max acc line.Length) 0
                let sideWidth = sideLines |> Seq.fold (fun acc line -> max acc line.Length) 0
                let minGap = max 1 gap
                if mainWidth + sideWidth + minGap <= availableWidth then
                    let totalRows = max mainLines.Length sideLines.Length
                    let spacing = max minGap (availableWidth - mainWidth - sideWidth)
                    let builder = StringBuilder()
                    for row in 0 .. totalRows - 1 do
                        if row > 0 then
                            builder.Append(Environment.NewLine) |> ignore
                        let mainLine = if row < mainLines.Length then mainLines[row] else ""
                        let sideLine = if row < sideLines.Length then sideLines[row] else ""
                        let paddedMain = mainLine.PadRight(mainWidth)
                        let paddedSide = sideLine.PadRight(sideWidth)
                        builder.Append(paddedMain) |> ignore
                        builder.Append(String(' ', spacing)) |> ignore
                        builder.Append(paddedSide) |> ignore
                    builder.ToString()
                else
                    mainContent + Environment.NewLine + sideContent
            ) widthAndMain side.Content
        { Content = content }

module Text =
    let ofString (value: string) =
        Widget.constant value

    let ofValue (value: IAdaptiveValue<string>) =
        Widget.text value

module Layout =
    let vstack = Widget.vstack
    let hstack = Widget.hstack
    let border = Widget.border
    let padLeft = Widget.padLeft
    let padRight = Widget.padRight
    let pad = Widget.pad
    let textbox = Widget.textbox
    let table = Widget.table
    let progressBar = Widget.progressBar
    let spinner = Widget.spinner
    let keyValue = Widget.keyValue
    let responsiveRow = Widget.responsiveRow
    let sidebar = Widget.sidebar

type WidgetModifier = Widget -> Widget

module Prop =
    let border (horizontal: char) (vertical: char) : WidgetModifier =
        Widget.border horizontal vertical

    let padLeft (width: int) (padChar: char) : WidgetModifier =
        Widget.padLeft width padChar

    let padRight (width: int) (padChar: char) : WidgetModifier =
        Widget.padRight width padChar

    let pad (left: int) (right: int) (padChar: char) : WidgetModifier =
        Widget.pad left right padChar

    let textbox (width: int) : WidgetModifier =
        Widget.textbox width

module View =
    let private apply (props: WidgetModifier list) (widget: Widget) =
        props |> List.fold (fun acc prop -> prop acc) widget

    let text (props: WidgetModifier list) (value: string) =
        Widget.constant value |> apply props

    let textValue (props: WidgetModifier list) (value: IAdaptiveValue<string>) =
        Widget.text value |> apply props

    let vstack (props: WidgetModifier list) (children: Widget list) =
        Widget.vstack children |> apply props

    let hstack (props: WidgetModifier list) (separator: string) (children: Widget list) =
        Widget.hstack separator children |> apply props

    let table (props: WidgetModifier list) (headers: string list) (rows: string list list) =
        Widget.table headers rows |> apply props

    let progressBar (props: WidgetModifier list) (width: int) (value: IAdaptiveValue<float>) =
        Widget.progressBar width value |> apply props

    let spinner (props: WidgetModifier list) (frames: string list) (index: IAdaptiveValue<int>) =
        Widget.spinner frames index |> apply props

    let keyValue (props: WidgetModifier list) (key: string) (value: IAdaptiveValue<string>) =
        Widget.keyValue key value |> apply props

    let responsiveRow (props: WidgetModifier list) (width: IAdaptiveValue<int>) (gap: int) (left: Widget) (right: Widget) =
        Widget.responsiveRow width gap left right |> apply props

    let sidebar (props: WidgetModifier list) (width: IAdaptiveValue<int>) (gap: int) (main: Widget) (side: Widget) =
        Widget.sidebar width gap main side |> apply props
