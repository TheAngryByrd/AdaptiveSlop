/// <summary>
/// AdaptiveSlop.Tui - A reactive terminal UI library built on adaptive values.
/// 
/// This library provides a declarative way to build terminal user interfaces
/// using the adaptive/reactive programming model from AdaptiveSlop.Core.
/// All UI elements automatically update when their underlying data changes.
/// </summary>
/// <remarks>
/// <para>
/// <b>Architecture Overview:</b>
/// </para>
/// <list type="bullet">
///   <item><description>Widget - The core UI element with reactive string content</description></item>
///   <item><description>Render - Console rendering with change detection</description></item>
///   <item><description>Input - Keyboard event handling</description></item>
///   <item><description>Focus - Focus management for interactive widgets</description></item>
///   <item><description>InputWidgets - Pre-built interactive widgets (TextInput, Checkbox, etc.)</description></item>
///   <item><description>App - Application lifecycle management</description></item>
/// </list>
/// <para>
/// <b>Quick Start:</b>
/// </para>
/// <code>
/// let state = App.createState()
/// let myWidget = Widget.constant "Hello, World!"
/// App.run App.defaultConfig state myWidget
/// </code>
/// </remarks>
namespace AdaptiveSlop.Tui

open System
open System.Text
open AdaptiveSlop.Core

/// <summary>
/// The fundamental UI element in AdaptiveSlop.Tui.
/// A Widget wraps reactive string content that automatically updates when dependencies change.
/// </summary>
/// <remarks>
/// Widgets are composable - they can be combined using layout functions like vstack and hstack.
/// The Content property is an adaptive value, meaning changes propagate automatically.
/// </remarks>
/// <example>
/// <code>
/// // Create a static widget
/// let static = Widget.constant "Hello"
/// 
/// // Create a dynamic widget from a changeable value
/// let counter = CVal.create 0
/// let dynamic = Widget.text (AVal.map string (CVal.value counter))
/// 
/// // Combine widgets vertically
/// let combined = Widget.vstack [static; dynamic]
/// </code>
/// </example>
type Widget =
    { /// <summary>The reactive string content of this widget.</summary>
      Content: IAdaptiveValue<string> }

/// <summary>
/// Core widget creation and transformation functions.
/// </summary>
/// <remarks>
/// <para>This module provides the building blocks for creating UI elements:</para>
/// <list type="bullet">
///   <item><description><c>text</c>, <c>constant</c> - Create basic text widgets</description></item>
///   <item><description><c>map</c>, <c>map2</c> - Transform widget content</description></item>
///   <item><description><c>vstack</c>, <c>hstack</c> - Layout widgets</description></item>
///   <item><description><c>border</c>, <c>pad*</c> - Add visual styling</description></item>
///   <item><description><c>progressBar</c>, <c>spinner</c>, <c>table</c> - Pre-built components</description></item>
/// </list>
/// </remarks>
module Widget =
    /// <summary>
    /// Combines multiple widgets with a separator between each.
    /// </summary>
    /// <param name="separator">The string to insert between widgets.</param>
    /// <param name="widgets">The list of widgets to combine.</param>
    /// <returns>An adaptive value containing the combined content.</returns>
    let private combineWith (separator: string) (widgets: Widget list) =
        let contentValues = widgets |> List.map (fun widget -> widget.Content)
        match contentValues with
        | [] -> AVal.constant ""
        | first :: rest ->
            rest
            |> List.fold (fun acc next ->
                AVal.map2 (fun left right -> left + separator + right) acc next
            ) first

    /// <summary>
    /// Creates a widget from an adaptive string value.
    /// The widget content updates automatically when the value changes.
    /// </summary>
    /// <param name="value">The adaptive string value to wrap.</param>
    /// <returns>A widget that displays the current value.</returns>
    /// <example>
    /// <code>
    /// let name = CVal.create "World"
    /// let greeting = Widget.text (AVal.map (sprintf "Hello, %s!") (CVal.value name))
    /// // Widget displays "Hello, World!"
    /// name.Set("F#")
    /// // Widget now displays "Hello, F#!"
    /// </code>
    /// </example>
    let text (value: IAdaptiveValue<string>) =
        { Content = value }

    /// <summary>
    /// Creates a widget with static, unchanging content.
    /// </summary>
    /// <param name="value">The static string to display.</param>
    /// <returns>A widget with constant content.</returns>
    /// <example>
    /// <code>
    /// let header = Widget.constant "=== My Application ==="
    /// </code>
    /// </example>
    let constant (value: string) =
        { Content = AVal.constant value }

    /// <summary>
    /// Transforms the content of a widget using a mapping function.
    /// </summary>
    /// <param name="f">The function to apply to the content.</param>
    /// <param name="widget">The widget to transform.</param>
    /// <returns>A new widget with transformed content.</returns>
    /// <example>
    /// <code>
    /// let upper = Widget.constant "hello" |> Widget.map (fun s -> s.ToUpper())
    /// // Displays "HELLO"
    /// </code>
    /// </example>
    let map (f: string -> string) (widget: Widget) =
        { Content = AVal.map f widget.Content }

    /// <summary>
    /// Combines two widgets using a binary function.
    /// </summary>
    /// <param name="f">The function to combine the two contents.</param>
    /// <param name="left">The first widget.</param>
    /// <param name="right">The second widget.</param>
    /// <returns>A new widget with combined content.</returns>
    let map2 (f: string -> string -> string) (left: Widget) (right: Widget) =
        { Content = AVal.map2 f left.Content right.Content }

    /// <summary>
    /// Stacks widgets vertically (one per line).
    /// </summary>
    /// <param name="widgets">The widgets to stack.</param>
    /// <returns>A widget containing all inputs stacked vertically.</returns>
    /// <example>
    /// <code>
    /// let menu = Widget.vstack [
    ///     Widget.constant "1. New Game"
    ///     Widget.constant "2. Load Game"
    ///     Widget.constant "3. Exit"
    /// ]
    /// </code>
    /// </example>
    let vstack (widgets: Widget list) =
        { Content = combineWith Environment.NewLine widgets }

    /// <summary>
    /// Stacks widgets horizontally with a custom separator.
    /// </summary>
    /// <param name="separator">The string to place between widgets.</param>
    /// <param name="widgets">The widgets to stack.</param>
    /// <returns>A widget containing all inputs in a horizontal row.</returns>
    /// <example>
    /// <code>
    /// let row = Widget.hstack " | " [
    ///     Widget.constant "Name"
    ///     Widget.constant "Age"
    ///     Widget.constant "City"
    /// ]
    /// // Displays: "Name | Age | City"
    /// </code>
    /// </example>
    let hstack (separator: string) (widgets: Widget list) =
        { Content = combineWith separator widgets }

    /// <summary>
    /// Pads the content on the left to reach a minimum width.
    /// </summary>
    /// <param name="width">The minimum total width.</param>
    /// <param name="padChar">The character to use for padding.</param>
    /// <param name="widget">The widget to pad.</param>
    /// <returns>A widget with left-padded content.</returns>
    let padLeft (width: int) (padChar: char) (widget: Widget) =
        map (fun value -> value.PadLeft(width, padChar)) widget

    /// <summary>
    /// Pads the content on the right to reach a minimum width.
    /// </summary>
    /// <param name="width">The minimum total width.</param>
    /// <param name="padChar">The character to use for padding.</param>
    /// <param name="widget">The widget to pad.</param>
    /// <returns>A widget with right-padded content.</returns>
    let padRight (width: int) (padChar: char) (widget: Widget) =
        map (fun value -> value.PadRight(width, padChar)) widget

    /// <summary>
    /// Pads the content on both sides.
    /// </summary>
    /// <param name="left">Number of padding characters on the left.</param>
    /// <param name="right">Number of padding characters on the right.</param>
    /// <param name="padChar">The character to use for padding.</param>
    /// <param name="widget">The widget to pad.</param>
    /// <returns>A widget with padding on both sides.</returns>
    let pad (left: int) (right: int) (padChar: char) (widget: Widget) =
        map (fun value ->
            let padded = value.PadLeft(value.Length + left, padChar)
            padded.PadRight(padded.Length + right, padChar)) widget

    /// <summary>
    /// Wraps a widget with a border using specified characters.
    /// </summary>
    /// <param name="horizontal">The character for horizontal border lines.</param>
    /// <param name="vertical">The character for vertical border sides.</param>
    /// <param name="widget">The widget to wrap.</param>
    /// <returns>A widget surrounded by a border.</returns>
    /// <example>
    /// <code>
    /// let boxed = Widget.constant "Hello" |> Widget.border '=' '|'
    /// // Displays:
    /// // =======
    /// // |Hello|
    /// // =======
    /// </code>
    /// </example>
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

    /// <summary>
    /// Constrains a widget to a fixed width, truncating or padding as needed.
    /// </summary>
    /// <param name="width">The exact width to enforce.</param>
    /// <param name="widget">The widget to constrain.</param>
    /// <returns>A widget with exactly the specified width.</returns>
    let textbox (width: int) (widget: Widget) =
        map (fun value ->
            if value.Length <= width then
                value.PadRight(width)
            else
                value.Substring(0, width)) widget

    /// <summary>
    /// Creates a static table widget from headers and rows.
    /// </summary>
    /// <param name="headers">The column header names.</param>
    /// <param name="rows">The data rows (list of lists).</param>
    /// <returns>A widget displaying a formatted ASCII table.</returns>
    /// <example>
    /// <code>
    /// let table = Widget.table 
    ///     ["Name"; "Age"; "City"]
    ///     [
    ///         ["Alice"; "30"; "NYC"]
    ///         ["Bob"; "25"; "LA"]
    ///     ]
    /// // Displays:
    /// // Name  | Age | City
    /// // ------+-----+-----
    /// // Alice | 30  | NYC
    /// // Bob   | 25  | LA
    /// </code>
    /// </example>
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

    /// <summary>
    /// Creates a reactive progress bar widget.
    /// </summary>
    /// <param name="width">The width of the bar portion (excluding brackets and percentage).</param>
    /// <param name="value">An adaptive float value between 0.0 and 1.0.</param>
    /// <returns>A widget displaying a progress bar like "[████░░░░░░] 40%".</returns>
    /// <example>
    /// <code>
    /// let progress = CVal.create 0.0
    /// let bar = Widget.progressBar 20 (CVal.value progress)
    /// progress.Set(0.5) // Bar shows 50%
    /// </code>
    /// </example>
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

    /// <summary>
    /// Creates an animated spinner widget.
    /// </summary>
    /// <param name="frames">The animation frames (e.g., ["|"; "/"; "-"; "\\"]).</param>
    /// <param name="index">An adaptive integer that cycles through frames.</param>
    /// <returns>A widget showing the current animation frame.</returns>
    /// <example>
    /// <code>
    /// let tick = CVal.create 0
    /// let spinner = Widget.spinner ["⠋"; "⠙"; "⠹"; "⠸"; "⠼"; "⠴"; "⠦"; "⠧"; "⠇"; "⠏"] (CVal.value tick)
    /// // Increment tick to animate
    /// </code>
    /// </example>
    let spinner (frames: string list) (index: IAdaptiveValue<int>) =
        let frameList = if frames.IsEmpty then [ "|" ] else frames
        let content =
            AVal.map (fun value ->
                let idx = abs value % frameList.Length
                frameList[idx]) index
        { Content = content }

    /// <summary>
    /// Creates a key-value display widget.
    /// </summary>
    /// <param name="key">The label/key to display.</param>
    /// <param name="value">The adaptive value to display.</param>
    /// <returns>A widget showing "key: value".</returns>
    /// <example>
    /// <code>
    /// let status = CVal.create "Running"
    /// let display = Widget.keyValue "Status" (CVal.value status)
    /// // Displays: "Status: Running"
    /// </code>
    /// </example>
    let keyValue (key: string) (value: IAdaptiveValue<string>) =
        map (fun v -> $"{key}: {v}") (text value)

    /// <summary>
    /// Creates a responsive row that switches between horizontal and vertical layout.
    /// When there's enough width, widgets appear side-by-side; otherwise they stack.
    /// </summary>
    /// <param name="width">Adaptive value of available width.</param>
    /// <param name="gap">Minimum gap between widgets when side-by-side.</param>
    /// <param name="left">The left widget.</param>
    /// <param name="right">The right widget.</param>
    /// <returns>A widget with responsive layout.</returns>
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

    /// <summary>
    /// Creates a sidebar layout (main content with side panel).
    /// Alias for responsiveRow with different semantics.
    /// </summary>
    /// <param name="width">Adaptive value of available width.</param>
    /// <param name="gap">Minimum gap between main and sidebar.</param>
    /// <param name="main">The main content widget.</param>
    /// <param name="side">The sidebar widget.</param>
    /// <returns>A widget with sidebar layout.</returns>
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

/// <summary>
/// Convenience functions for creating text widgets.
/// </summary>
module Text =
    /// <summary>Creates a constant text widget from a string.</summary>
    /// <param name="value">The text to display.</param>
    let ofString (value: string) =
        Widget.constant value

    /// <summary>Creates a reactive text widget from an adaptive value.</summary>
    /// <param name="value">The adaptive string value.</param>
    let ofValue (value: IAdaptiveValue<string>) =
        Widget.text value

/// <summary>
/// Aliases for layout-related widget functions.
/// Provides a more discoverable API for common layout operations.
/// </summary>
module Layout =
    /// <summary>Stack widgets vertically.</summary>
    let vstack = Widget.vstack
    /// <summary>Stack widgets horizontally with separator.</summary>
    let hstack = Widget.hstack
    /// <summary>Add a border around a widget.</summary>
    let border = Widget.border
    /// <summary>Pad content on the left.</summary>
    let padLeft = Widget.padLeft
    /// <summary>Pad content on the right.</summary>
    let padRight = Widget.padRight
    /// <summary>Pad content on both sides.</summary>
    let pad = Widget.pad
    /// <summary>Constrain to fixed width.</summary>
    let textbox = Widget.textbox
    /// <summary>Create an ASCII table.</summary>
    let table = Widget.table
    /// <summary>Create a progress bar.</summary>
    let progressBar = Widget.progressBar
    /// <summary>Create an animated spinner.</summary>
    let spinner = Widget.spinner
    /// <summary>Create a key-value display.</summary>
    let keyValue = Widget.keyValue
    /// <summary>Create a responsive horizontal row.</summary>
    let responsiveRow = Widget.responsiveRow
    /// <summary>Create a main+sidebar layout.</summary>
    let sidebar = Widget.sidebar

/// <summary>
/// Function type for modifying widgets.
/// Used with the View module for declarative widget construction.
/// </summary>
type WidgetModifier = Widget -> Widget

/// <summary>
/// Widget modifier functions that can be composed and applied.
/// Use with View module functions for declarative styling.
/// </summary>
/// <example>
/// <code>
/// let styledText = View.text [Prop.border '=' '|'; Prop.padLeft 2 ' '] "Hello"
/// </code>
/// </example>
module Prop =
    /// <summary>Creates a border modifier.</summary>
    let border (horizontal: char) (vertical: char) : WidgetModifier =
        Widget.border horizontal vertical

    /// <summary>Creates a left-padding modifier.</summary>
    let padLeft (width: int) (padChar: char) : WidgetModifier =
        Widget.padLeft width padChar

    /// <summary>Creates a right-padding modifier.</summary>
    let padRight (width: int) (padChar: char) : WidgetModifier =
        Widget.padRight width padChar

    /// <summary>Creates a two-sided padding modifier.</summary>
    let pad (left: int) (right: int) (padChar: char) : WidgetModifier =
        Widget.pad left right padChar

    /// <summary>Creates a fixed-width textbox modifier.</summary>
    let textbox (width: int) : WidgetModifier =
        Widget.textbox width

/// <summary>
/// High-level widget constructors with modifier support.
/// Provides a more declarative API similar to UI frameworks.
/// </summary>
/// <example>
/// <code>
/// // Create a bordered, padded text widget
/// let fancy = View.text [Prop.border '-' '|'] "Hello"
/// 
/// // Create a vertical stack with a border
/// let menu = View.vstack [Prop.border '=' '|'] [
///     Widget.constant "Option 1"
///     Widget.constant "Option 2"
/// ]
/// </code>
/// </example>
module View =
    let private apply (props: WidgetModifier list) (widget: Widget) =
        props |> List.fold (fun acc prop -> prop acc) widget

    /// <summary>Creates a constant text widget with modifiers.</summary>
    let text (props: WidgetModifier list) (value: string) =
        Widget.constant value |> apply props

    /// <summary>Creates a reactive text widget with modifiers.</summary>
    let textValue (props: WidgetModifier list) (value: IAdaptiveValue<string>) =
        Widget.text value |> apply props

    /// <summary>Creates a vertical stack with modifiers.</summary>
    let vstack (props: WidgetModifier list) (children: Widget list) =
        Widget.vstack children |> apply props

    /// <summary>Creates a horizontal stack with modifiers.</summary>
    let hstack (props: WidgetModifier list) (separator: string) (children: Widget list) =
        Widget.hstack separator children |> apply props

    /// <summary>Creates a table widget with modifiers.</summary>
    let table (props: WidgetModifier list) (headers: string list) (rows: string list list) =
        Widget.table headers rows |> apply props

    /// <summary>Creates a progress bar with modifiers.</summary>
    let progressBar (props: WidgetModifier list) (width: int) (value: IAdaptiveValue<float>) =
        Widget.progressBar width value |> apply props

    /// <summary>Creates a spinner with modifiers.</summary>
    let spinner (props: WidgetModifier list) (frames: string list) (index: IAdaptiveValue<int>) =
        Widget.spinner frames index |> apply props

    /// <summary>Creates a key-value display with modifiers.</summary>
    let keyValue (props: WidgetModifier list) (key: string) (value: IAdaptiveValue<string>) =
        Widget.keyValue key value |> apply props

    /// <summary>Creates a responsive row with modifiers.</summary>
    let responsiveRow (props: WidgetModifier list) (width: IAdaptiveValue<int>) (gap: int) (left: Widget) (right: Widget) =
        Widget.responsiveRow width gap left right |> apply props

    /// <summary>Creates a sidebar layout with modifiers.</summary>
    let sidebar (props: WidgetModifier list) (width: IAdaptiveValue<int>) (gap: int) (main: Widget) (side: Widget) =
        Widget.sidebar width gap main side |> apply props

// =============================================================================
// RENDER MODULE - Console rendering infrastructure
// =============================================================================

/// <summary>
/// Tracks the current console window dimensions as reactive values.
/// Automatically updates when the window is resized.
/// </summary>
type WindowDimensions = {
    /// <summary>The current window width in characters.</summary>
    Width: ChangeableValue<int>
    /// <summary>The current window height in lines.</summary>
    Height: ChangeableValue<int>
}

/// <summary>
/// Functions for managing window dimensions.
/// </summary>
module WindowDimensions =
    /// <summary>
    /// Creates a new WindowDimensions initialized to current console size.
    /// </summary>
    let create () = {
        Width = CVal.create Console.WindowWidth
        Height = CVal.create Console.WindowHeight
    }
    
    /// <summary>
    /// Updates the dimensions if the console size has changed.
    /// Call this periodically (e.g., in a render timer) to track resizes.
    /// </summary>
    /// <param name="dims">The WindowDimensions to update.</param>
    let update (dims: WindowDimensions) =
        let currentWidth = Console.WindowWidth
        let currentHeight = Console.WindowHeight
        if currentWidth <> AVal.getValue (CVal.value dims.Width) then
            dims.Width.Set(currentWidth)
        if currentHeight <> AVal.getValue (CVal.value dims.Height) then
            dims.Height.Set(currentHeight)

/// <summary>
/// Console rendering utilities with change detection.
/// </summary>
/// <remarks>
/// The Render module provides efficient console output by:
/// <list type="bullet">
///   <item><description>Only updating when content actually changes</description></item>
///   <item><description>Padding content to fill the window (preventing artifacts)</description></item>
///   <item><description>Thread-safe rendering with locking</description></item>
/// </list>
/// </remarks>
module Render =
    open System.Threading
    
    /// <summary>
    /// Pads widget content to fill the entire window.
    /// Handles truncation for content wider/taller than the window.
    /// </summary>
    /// <param name="width">Adaptive window width.</param>
    /// <param name="height">Adaptive window height.</param>
    /// <param name="widget">The widget to pad.</param>
    /// <returns>Adaptive string content padded to fill the window.</returns>
    let padToWindow (width: IAdaptiveValue<int>) (height: IAdaptiveValue<int>) (widget: Widget) : IAdaptiveValue<string> =
        let widthAndContent : IAdaptiveValue<int * string> =
            AVal.map2 (fun (w: int) (text: string) -> w, text) width widget.Content
        AVal.map2 (fun (h: int) ((w, text): int * string) ->
            let lines = text.Split([|"\r\n"; "\n"|], StringSplitOptions.None)
            let paddedLines = lines |> Array.map (fun line -> 
                if line.Length >= w then line.Substring(0, w)
                else line.PadRight(w))
            if paddedLines.Length >= h then
                paddedLines |> Array.take h |> String.concat Environment.NewLine
            else
                let blankLine = String(' ', w)
                Array.concat [ paddedLines; Array.create (h - paddedLines.Length) blankLine ]
                |> String.concat Environment.NewLine
        ) height widthAndContent
    
    /// <summary>
    /// Creates a renderer function with change detection.
    /// The returned function only writes to the console when content has changed.
    /// Thread-safe via internal locking.
    /// </summary>
    /// <returns>A render function that takes adaptive content and updates the console.</returns>
    /// <example>
    /// <code>
    /// let renderer = Render.createRenderer()
    /// // In a timer callback:
    /// renderer paddedContent
    /// </code>
    /// </example>
    let createRenderer () =
        let renderLock = obj()
        let mutable lastOutput: string option = None
        fun (content: IAdaptiveValue<string>) ->
            if Monitor.TryEnter(renderLock) then
                try
                    let output = AVal.getValue content
                    if lastOutput <> Some output then
                        Console.SetCursorPosition(0, 0)
                        Console.Write(output)
                        lastOutput <- Some output
                finally
                    Monitor.Exit(renderLock)
    
    /// <summary>
    /// Renders a widget to the console once (no change detection).
    /// Useful for simple, non-interactive output.
    /// </summary>
    /// <param name="widget">The widget to render.</param>
    let renderOnce (widget: Widget) =
        Console.Write(AVal.getValue widget.Content)

// =============================================================================
// INPUT MODULE - Key event types and handling
// =============================================================================

/// <summary>
/// Keyboard input handling for interactive TUI applications.
/// </summary>
/// <remarks>
/// <para>This module provides:</para>
/// <list type="bullet">
///   <item><description>KeyEvent type wrapping ConsoleKeyInfo with better ergonomics</description></item>
///   <item><description>KeyHandler function type for event callbacks</description></item>
///   <item><description>Blocking and non-blocking key reading</description></item>
/// </list>
/// </remarks>
module Input =
    /// <summary>
    /// Represents a keyboard event with key code, character, and modifiers.
    /// </summary>
    type KeyEvent = {
        /// <summary>The ConsoleKey value (e.g., ConsoleKey.Enter, ConsoleKey.A).</summary>
        Key: ConsoleKey
        /// <summary>The character produced by the key (if any).</summary>
        KeyChar: char
        /// <summary>Modifier keys held (Shift, Ctrl, Alt).</summary>
        Modifiers: ConsoleModifiers
    }
    
    /// <summary>
    /// Converts a ConsoleKeyInfo to a KeyEvent.
    /// </summary>
    /// <param name="info">The ConsoleKeyInfo from Console.ReadKey().</param>
    /// <returns>A KeyEvent record.</returns>
    let fromConsoleKey (info: ConsoleKeyInfo) : KeyEvent = {
        Key = info.Key
        KeyChar = info.KeyChar
        Modifiers = info.Modifiers
    }
    
    /// <summary>
    /// Function type for handling key events.
    /// Returns true if the event was handled, false to allow propagation.
    /// </summary>
    type KeyHandler = KeyEvent -> bool
    
    /// <summary>
    /// Checks if a key event represents a printable character.
    /// </summary>
    /// <param name="event">The key event to check.</param>
    /// <returns>True if the key produces a printable character.</returns>
    let isPrintable (event: KeyEvent) =
        not (Char.IsControl(event.KeyChar)) && event.KeyChar <> '\000'
    
    /// <summary>
    /// Reads a key if one is available (non-blocking).
    /// </summary>
    /// <returns>Some KeyEvent if a key was pressed, None otherwise.</returns>
    let tryReadKey () =
        if Console.KeyAvailable then
            Some (Console.ReadKey(true) |> fromConsoleKey)
        else
            None
    
    /// <summary>
    /// Reads a key, blocking until one is pressed.
    /// </summary>
    /// <returns>The KeyEvent for the pressed key.</returns>
    let readKey () =
        Console.ReadKey(true) |> fromConsoleKey

// =============================================================================
// FOCUS MODULE - Focus management for input widgets
// =============================================================================

/// <summary>
/// Focus management for interactive widgets.
/// Tracks which widget has keyboard focus and provides Tab navigation.
/// </summary>
/// <remarks>
/// <para>Focus flow:</para>
/// <list type="number">
///   <item><description>Register widgets with unique FocusIds</description></item>
///   <item><description>Use focusNext/focusPrev for Tab navigation</description></item>
///   <item><description>Query hasFocus to render focused state</description></item>
/// </list>
/// </remarks>
module Focus =
    /// <summary>
    /// Unique identifier for a focusable widget.
    /// </summary>
    type FocusId = FocusId of string
    
    /// <summary>
    /// State tracking for focus management.
    /// </summary>
    type FocusState = {
        /// <summary>The currently focused widget, if any.</summary>
        CurrentFocus: ChangeableValue<FocusId option>
        /// <summary>The ordered list of focusable widgets (Tab order).</summary>
        FocusOrder: ChangeableValue<FocusId list>
    }
    
    /// <summary>
    /// Creates a new, empty focus state.
    /// </summary>
    let create () : FocusState = {
        CurrentFocus = CVal.create None
        FocusOrder = CVal.create []
    }
    
    /// <summary>
    /// Registers a widget as focusable.
    /// Widgets are focused in registration order when using Tab.
    /// </summary>
    /// <param name="id">The unique identifier for this widget.</param>
    /// <param name="state">The focus state to register with.</param>
    let register (id: FocusId) (state: FocusState) =
        let order = AVal.getValue (CVal.value state.FocusOrder)
        if not (List.contains id order) then
            state.FocusOrder.Set(order @ [id])
    
    /// <summary>
    /// Unregisters a widget, removing it from focus order.
    /// Clears focus if this widget was focused.
    /// </summary>
    /// <param name="id">The widget identifier to remove.</param>
    /// <param name="state">The focus state to modify.</param>
    let unregister (id: FocusId) (state: FocusState) =
        let order = AVal.getValue (CVal.value state.FocusOrder)
        state.FocusOrder.Set(List.filter ((<>) id) order)
        let current = AVal.getValue (CVal.value state.CurrentFocus)
        if current = Some id then
            state.CurrentFocus.Set(None)
    
    /// <summary>
    /// Sets focus to a specific widget.
    /// </summary>
    /// <param name="id">The widget to focus.</param>
    /// <param name="state">The focus state to modify.</param>
    let setFocus (id: FocusId) (state: FocusState) =
        state.CurrentFocus.Set(Some id)
    
    /// <summary>
    /// Clears focus (no widget focused).
    /// </summary>
    /// <param name="state">The focus state to modify.</param>
    let clearFocus (state: FocusState) =
        state.CurrentFocus.Set(None)
    
    /// <summary>
    /// Returns an adaptive boolean indicating if a widget has focus.
    /// Use this to reactively render focused/unfocused states.
    /// </summary>
    /// <param name="id">The widget identifier to check.</param>
    /// <param name="state">The focus state to query.</param>
    /// <returns>Adaptive bool that updates when focus changes.</returns>
    let hasFocus (id: FocusId) (state: FocusState) : IAdaptiveValue<bool> =
        AVal.map (fun current -> current = Some id) (CVal.value state.CurrentFocus)
    
    /// <summary>
    /// Moves focus to the next widget in Tab order.
    /// Wraps around to the first widget after the last.
    /// </summary>
    /// <param name="state">The focus state to modify.</param>
    let focusNext (state: FocusState) =
        let order = AVal.getValue (CVal.value state.FocusOrder)
        let current = AVal.getValue (CVal.value state.CurrentFocus)
        match order, current with
        | [], _ -> ()
        | first :: _, None -> state.CurrentFocus.Set(Some first)
        | _, Some id ->
            match List.tryFindIndex ((=) id) order with
            | Some idx ->
                let nextIdx = (idx + 1) % order.Length
                state.CurrentFocus.Set(Some order.[nextIdx])
            | None ->
                state.CurrentFocus.Set(Some order.[0])
    
    /// <summary>
    /// Moves focus to the previous widget in Tab order.
    /// Wraps around to the last widget before the first.
    /// </summary>
    /// <param name="state">The focus state to modify.</param>
    let focusPrev (state: FocusState) =
        let order = AVal.getValue (CVal.value state.FocusOrder)
        let current = AVal.getValue (CVal.value state.CurrentFocus)
        match order, current with
        | [], _ -> ()
        | _, None -> state.CurrentFocus.Set(Some (List.last order))
        | _, Some id ->
            match List.tryFindIndex ((=) id) order with
            | Some idx ->
                let prevIdx = if idx = 0 then order.Length - 1 else idx - 1
                state.CurrentFocus.Set(Some order.[prevIdx])
            | None ->
                state.CurrentFocus.Set(Some (List.last order))

// =============================================================================
// INPUT CONTEXT - Combines focus and key handling
// =============================================================================

/// <summary>
/// Combines focus management with keyboard event routing.
/// Provides a unified system for handling input across widgets.
/// </summary>
/// <remarks>
/// <para>Event dispatch order:</para>
/// <list type="number">
///   <item><description>Global handlers (e.g., quit on 'Q')</description></item>
///   <item><description>Focused widget's handler</description></item>
/// </list>
/// <para>If any handler returns true, the event is considered handled.</para>
/// </remarks>
module InputContext =
    /// <summary>
    /// Complete input context with focus and handlers.
    /// </summary>
    type Context = {
        /// <summary>Focus state for tracking current focus.</summary>
        FocusState: Focus.FocusState
        /// <summary>Per-widget key handlers keyed by FocusId.</summary>
        Handlers: ChangeableValue<Map<Focus.FocusId, Input.KeyHandler>>
        /// <summary>Global handlers that run before widget handlers.</summary>
        GlobalHandlers: ChangeableValue<Input.KeyHandler list>
    }
    
    /// <summary>
    /// Creates a new, empty input context.
    /// </summary>
    let create () : Context = {
        FocusState = Focus.create ()
        Handlers = CVal.create Map.empty
        GlobalHandlers = CVal.create []
    }
    
    /// <summary>
    /// Registers a key handler for a focusable widget.
    /// Also registers the widget in focus order.
    /// </summary>
    /// <param name="id">The widget's focus identifier.</param>
    /// <param name="handler">The key handler function.</param>
    /// <param name="ctx">The input context to register with.</param>
    let registerHandler (id: Focus.FocusId) (handler: Input.KeyHandler) (ctx: Context) =
        let handlers = AVal.getValue (CVal.value ctx.Handlers)
        ctx.Handlers.Set(Map.add id handler handlers)
        Focus.register id ctx.FocusState
    
    /// <summary>
    /// Unregisters a widget's key handler and removes it from focus order.
    /// </summary>
    /// <param name="id">The widget's focus identifier.</param>
    /// <param name="ctx">The input context to modify.</param>
    let unregisterHandler (id: Focus.FocusId) (ctx: Context) =
        let handlers = AVal.getValue (CVal.value ctx.Handlers)
        ctx.Handlers.Set(Map.remove id handlers)
        Focus.unregister id ctx.FocusState
    
    /// <summary>
    /// Registers a global key handler that runs before widget handlers.
    /// Use for application-wide shortcuts (e.g., quit, help).
    /// </summary>
    /// <param name="handler">The global key handler.</param>
    /// <param name="ctx">The input context to modify.</param>
    let registerGlobalHandler (handler: Input.KeyHandler) (ctx: Context) =
        let handlers = AVal.getValue (CVal.value ctx.GlobalHandlers)
        ctx.GlobalHandlers.Set(handler :: handlers)
    
    /// <summary>
    /// Dispatches a key event through global handlers, then to the focused widget.
    /// </summary>
    /// <param name="event">The key event to dispatch.</param>
    /// <param name="ctx">The input context to use.</param>
    /// <returns>True if any handler consumed the event.</returns>
    let dispatch (event: Input.KeyEvent) (ctx: Context) : bool =
        // Try global handlers first
        let globalHandlers = AVal.getValue (CVal.value ctx.GlobalHandlers)
        let handledGlobally = globalHandlers |> List.exists (fun h -> h event)
        if handledGlobally then true
        else
            // Then try focused widget
            let focus = AVal.getValue (CVal.value ctx.FocusState.CurrentFocus)
            let handlers = AVal.getValue (CVal.value ctx.Handlers)
            match focus with
            | Some id ->
                match Map.tryFind id handlers with
                | Some handler -> handler event
                | None -> false
            | None -> false
    
    /// <summary>
    /// Handles Tab and Shift+Tab for focus navigation.
    /// </summary>
    /// <param name="event">The key event to check.</param>
    /// <param name="ctx">The input context to modify.</param>
    /// <returns>True if this was a Tab navigation event.</returns>
    let handleFocusNavigation (event: Input.KeyEvent) (ctx: Context) : bool =
        match event.Key with
        | ConsoleKey.Tab when event.Modifiers.HasFlag(ConsoleModifiers.Shift) ->
            Focus.focusPrev ctx.FocusState
            true
        | ConsoleKey.Tab ->
            Focus.focusNext ctx.FocusState
            true
        | _ -> false

// =============================================================================
// INPUT WIDGETS - Text input, checkbox, radio, select, button
// =============================================================================

/// <summary>
/// Pre-built interactive widgets for common UI patterns.
/// </summary>
/// <remarks>
/// <para>Each widget type provides:</para>
/// <list type="bullet">
///   <item><description>State type - Holds the widget's mutable data</description></item>
///   <item><description>create* function - Initializes widget state</description></item>
///   <item><description>Widget function - Renders state to a Widget</description></item>
///   <item><description>*Handler function - Handles keyboard input</description></item>
/// </list>
/// <para>
/// <b>Usage pattern:</b>
/// </para>
/// <code>
/// // 1. Create state
/// let state = InputWidgets.createTextInput "placeholder" "initial"
/// 
/// // 2. Register handler with App
/// let focusId = App.registerWidget "myInput" (InputWidgets.textInputHandler state) appState
/// 
/// // 3. Get focus state for rendering
/// let focused = App.getFocused "myInput" appState
/// 
/// // 4. Create widget
/// let widget = InputWidgets.textInput 20 focused state
/// </code>
/// </remarks>
module InputWidgets =
    
    // -------------------------------------------------------------------------
    // TEXT INPUT
    // -------------------------------------------------------------------------
    
    /// <summary>
    /// State for a text input widget with cursor support.
    /// </summary>
    type TextInputState = {
        /// <summary>The current text value.</summary>
        Value: ChangeableValue<string>
        /// <summary>The cursor position (0 = before first char).</summary>
        CursorPosition: ChangeableValue<int>
        /// <summary>Placeholder text shown when empty and unfocused.</summary>
        Placeholder: string
    }
    
    /// <summary>
    /// Creates a new text input state.
    /// </summary>
    /// <param name="placeholder">Text shown when empty and unfocused.</param>
    /// <param name="initial">Initial text value.</param>
    /// <returns>A new TextInputState.</returns>
    let createTextInput (placeholder: string) (initial: string) : TextInputState = {
        Value = CVal.create initial
        CursorPosition = CVal.create initial.Length
        Placeholder = placeholder
    }
    
    /// <summary>
    /// Creates a text input widget from state.
    /// Displays as: ">[text_content]&lt;" when focused.
    /// Shows cursor as underscore (_) at cursor position.
    /// </summary>
    /// <param name="width">Display width for the input field.</param>
    /// <param name="focused">Adaptive bool for focus state.</param>
    /// <param name="state">The text input state.</param>
    /// <returns>A Widget rendering the text input.</returns>
    let textInput (width: int) (focused: IAdaptiveValue<bool>) (state: TextInputState) : Widget =
        let content =
            AVal.map3 (fun value cursor isFocused ->
                let displayValue =
                    if String.IsNullOrEmpty(value) && not isFocused then
                        state.Placeholder
                    else
                        // Insert cursor character at position when focused
                        if isFocused && cursor <= value.Length then
                            let before = if cursor > 0 then value.Substring(0, min cursor value.Length) else ""
                            let after = if cursor < value.Length then value.Substring(cursor) else ""
                            before + "_" + after
                        else
                            value
                let padded = displayValue.PadRight(width)
                let truncated = if padded.Length > width then padded.Substring(0, width) else padded
                let prefix = if isFocused then ">" else " "
                let suffix = if isFocused then "<" else " "
                $"{prefix}[{truncated}]{suffix}"
            ) (CVal.value state.Value) (CVal.value state.CursorPosition) focused
        { Content = content }
    
    /// <summary>
    /// Key handler for text input. Handles:
    /// - Left/Right arrows: Move cursor
    /// - Home/End: Jump to start/end
    /// - Backspace: Delete before cursor
    /// - Delete: Delete at cursor
    /// - Printable chars: Insert at cursor
    /// </summary>
    /// <param name="state">The text input state to modify.</param>
    /// <param name="event">The key event.</param>
    /// <returns>True if the event was handled.</returns>
    let textInputHandler (state: TextInputState) (event: Input.KeyEvent) : bool =
        let value = AVal.getValue (CVal.value state.Value)
        let cursor = AVal.getValue (CVal.value state.CursorPosition)
        match event.Key with
        | ConsoleKey.LeftArrow ->
            if cursor > 0 then
                state.CursorPosition.Set(cursor - 1)
            true
        | ConsoleKey.RightArrow ->
            if cursor < value.Length then
                state.CursorPosition.Set(cursor + 1)
            true
        | ConsoleKey.Home ->
            state.CursorPosition.Set(0)
            true
        | ConsoleKey.End ->
            state.CursorPosition.Set(value.Length)
            true
        | ConsoleKey.Backspace ->
            if cursor > 0 then
                let newValue = value.Remove(cursor - 1, 1)
                state.Value.Set(newValue)
                state.CursorPosition.Set(cursor - 1)
            true
        | ConsoleKey.Delete ->
            if cursor < value.Length then
                let newValue = value.Remove(cursor, 1)
                state.Value.Set(newValue)
            true
        | _ when Input.isPrintable event ->
            let newValue = value.Insert(cursor, string event.KeyChar)
            state.Value.Set(newValue)
            state.CursorPosition.Set(cursor + 1)
            true
        | _ -> false
    
    // -------------------------------------------------------------------------
    // CHECKBOX
    // -------------------------------------------------------------------------
    
    /// <summary>
    /// State for a checkbox widget.
    /// </summary>
    type CheckboxState = {
        /// <summary>Whether the checkbox is checked.</summary>
        Checked: ChangeableValue<bool>
        /// <summary>The label displayed next to the checkbox.</summary>
        Label: string
    }
    
    /// <summary>
    /// Creates a new checkbox state.
    /// </summary>
    /// <param name="label">The label to display.</param>
    /// <param name="initial">Initial checked state.</param>
    /// <returns>A new CheckboxState.</returns>
    let createCheckbox (label: string) (initial: bool) : CheckboxState = {
        Checked = CVal.create initial
        Label = label
    }
    
    /// <summary>
    /// Creates a checkbox widget from state.
    /// Displays as: ">[X] Label&lt;" when checked and focused.
    /// </summary>
    /// <param name="focused">Adaptive bool for focus state.</param>
    /// <param name="state">The checkbox state.</param>
    /// <returns>A Widget rendering the checkbox.</returns>
    let checkbox (focused: IAdaptiveValue<bool>) (state: CheckboxState) : Widget =
        let content =
            AVal.map2 (fun isChecked isFocused ->
                let marker = if isChecked then "[X]" else "[ ]"
                let prefix = if isFocused then ">" else " "
                let suffix = if isFocused then "<" else " "
                $"{prefix}{marker} {state.Label}{suffix}"
            ) (CVal.value state.Checked) focused
        { Content = content }
    
    /// <summary>
    /// Key handler for checkbox. Toggles on Space or Enter.
    /// </summary>
    /// <param name="state">The checkbox state to modify.</param>
    /// <param name="event">The key event.</param>
    /// <returns>True if the event was handled.</returns>
    let checkboxHandler (state: CheckboxState) (event: Input.KeyEvent) : bool =
        match event.Key with
        | ConsoleKey.Spacebar | ConsoleKey.Enter ->
            let current = AVal.getValue (CVal.value state.Checked)
            state.Checked.Set(not current)
            true
        | _ -> false
    
    // -------------------------------------------------------------------------
    // RADIO GROUP
    // -------------------------------------------------------------------------
    
    /// <summary>
    /// An item in a radio group.
    /// </summary>
    type RadioItem = {
        /// <summary>The display label.</summary>
        Label: string
        /// <summary>The value returned when selected.</summary>
        Value: string
    }
    
    /// <summary>
    /// State for a radio group widget (single selection from list).
    /// </summary>
    type RadioGroupState = {
        /// <summary>The available options.</summary>
        Items: RadioItem list
        /// <summary>Index of the currently selected item.</summary>
        SelectedIndex: ChangeableValue<int>
        /// <summary>Index of the currently focused item (for arrow navigation).</summary>
        FocusedIndex: ChangeableValue<int>
    }
    
    /// <summary>
    /// Creates a new radio group state.
    /// </summary>
    /// <param name="items">The list of options.</param>
    /// <param name="initialIndex">Index of the initially selected item.</param>
    /// <returns>A new RadioGroupState.</returns>
    let createRadioGroup (items: RadioItem list) (initialIndex: int) : RadioGroupState = {
        Items = items
        SelectedIndex = CVal.create initialIndex
        FocusedIndex = CVal.create initialIndex
    }
    
    /// <summary>
    /// Creates a radio group widget from state.
    /// Displays as a vertical list with (o) for selected, ( ) for unselected.
    /// </summary>
    /// <param name="focused">Adaptive bool for focus state.</param>
    /// <param name="state">The radio group state.</param>
    /// <returns>A Widget rendering the radio group.</returns>
    let radioGroup (focused: IAdaptiveValue<bool>) (state: RadioGroupState) : Widget =
        let content =
            AVal.map3 (fun selectedIdx focusedIdx isFocused ->
                state.Items
                |> List.mapi (fun idx item ->
                    let isSelected = idx = selectedIdx
                    let isFocusedItem = isFocused && idx = focusedIdx
                    let marker = if isSelected then "(o)" else "( )"
                    let prefix = if isFocusedItem then ">" else " "
                    let suffix = if isFocusedItem then "<" else " "
                    $"{prefix}{marker} {item.Label}{suffix}")
                |> String.concat Environment.NewLine
            ) (CVal.value state.SelectedIndex) (CVal.value state.FocusedIndex) focused
        { Content = content }
    
    /// <summary>
    /// Gets the currently selected value as an adaptive string.
    /// </summary>
    /// <param name="state">The radio group state.</param>
    /// <returns>Adaptive string with the selected item's Value.</returns>
    let radioGroupValue (state: RadioGroupState) : IAdaptiveValue<string> =
        AVal.map (fun idx ->
            if idx >= 0 && idx < state.Items.Length then
                state.Items.[idx].Value
            else
                ""
        ) (CVal.value state.SelectedIndex)
    
    /// <summary>
    /// Key handler for radio group. Handles:
    /// - Up/Down: Navigate between options
    /// - Space/Enter: Select the focused option
    /// </summary>
    /// <param name="state">The radio group state to modify.</param>
    /// <param name="event">The key event.</param>
    /// <returns>True if the event was handled.</returns>
    let radioGroupHandler (state: RadioGroupState) (event: Input.KeyEvent) : bool =
        let focusedIdx = AVal.getValue (CVal.value state.FocusedIndex)
        let itemCount = state.Items.Length
        match event.Key with
        | ConsoleKey.UpArrow ->
            let newIdx = if focusedIdx > 0 then focusedIdx - 1 else itemCount - 1
            state.FocusedIndex.Set(newIdx)
            true
        | ConsoleKey.DownArrow ->
            let newIdx = if focusedIdx < itemCount - 1 then focusedIdx + 1 else 0
            state.FocusedIndex.Set(newIdx)
            true
        | ConsoleKey.Spacebar | ConsoleKey.Enter ->
            state.SelectedIndex.Set(focusedIdx)
            true
        | _ -> false
    
    // -------------------------------------------------------------------------
    // SELECT (Dropdown-style list)
    // -------------------------------------------------------------------------
    
    /// <summary>
    /// State for a select (dropdown) widget.
    /// </summary>
    type SelectState = {
        /// <summary>The available options.</summary>
        Items: string list
        /// <summary>Index of the currently selected item.</summary>
        SelectedIndex: ChangeableValue<int>
        /// <summary>Whether the dropdown is open.</summary>
        IsOpen: ChangeableValue<bool>
        /// <summary>Index of the focused item when open.</summary>
        FocusedIndex: ChangeableValue<int>
        /// <summary>Maximum items to show when open.</summary>
        MaxVisible: int
    }
    
    /// <summary>
    /// Creates a new select state.
    /// </summary>
    /// <param name="items">The list of options.</param>
    /// <param name="initialIndex">Index of the initially selected item.</param>
    /// <param name="maxVisible">Maximum items to display when open.</param>
    /// <returns>A new SelectState.</returns>
    let createSelect (items: string list) (initialIndex: int) (maxVisible: int) : SelectState = {
        Items = items
        SelectedIndex = CVal.create initialIndex
        IsOpen = CVal.create false
        FocusedIndex = CVal.create initialIndex
        MaxVisible = maxVisible
    }
    
    /// <summary>
    /// Creates a select widget from state.
    /// Displays as a single line with dropdown arrow; expands when open.
    /// </summary>
    /// <param name="width">Display width for the select field.</param>
    /// <param name="focused">Adaptive bool for focus state.</param>
    /// <param name="state">The select state.</param>
    /// <returns>A Widget rendering the select.</returns>
    let select (width: int) (focused: IAdaptiveValue<bool>) (state: SelectState) : Widget =
        let content =
            AVal.map4 (fun selectedIdx isOpen focusedIdx isFocused ->
                let selectedText =
                    if selectedIdx >= 0 && selectedIdx < state.Items.Length then
                        state.Items.[selectedIdx]
                    else
                        ""
                let prefix = if isFocused then ">" else " "
                let suffix = if isFocused then "<" else " "
                let arrow = if isOpen then "^" else "v"
                let header = 
                    let padded = selectedText.PadRight(width - 2)
                    let truncated = if padded.Length > width - 2 then padded.Substring(0, width - 2) else padded
                    $"{prefix}[{truncated}{arrow}]{suffix}"
                
                if isOpen && isFocused then
                    let startIdx = max 0 (focusedIdx - state.MaxVisible / 2)
                    let endIdx = min state.Items.Length (startIdx + state.MaxVisible)
                    let adjustedStart = max 0 (endIdx - state.MaxVisible)
                    let visibleItems =
                        state.Items
                        |> List.skip adjustedStart
                        |> List.take (min state.MaxVisible (state.Items.Length - adjustedStart))
                        |> List.mapi (fun i item ->
                            let actualIdx = adjustedStart + i
                            let isFocusedItem = actualIdx = focusedIdx
                            let marker = if isFocusedItem then ">" else " "
                            let padded = item.PadRight(width)
                            let truncated = if padded.Length > width then padded.Substring(0, width) else padded
                            $" {marker}{truncated}")
                        |> String.concat Environment.NewLine
                    header + Environment.NewLine + visibleItems
                else
                    header
            ) (CVal.value state.SelectedIndex) (CVal.value state.IsOpen) (CVal.value state.FocusedIndex) focused
        { Content = content }
    
    /// <summary>
    /// Gets the currently selected value as an adaptive string.
    /// </summary>
    /// <param name="state">The select state.</param>
    /// <returns>Adaptive string with the selected item.</returns>
    let selectValue (state: SelectState) : IAdaptiveValue<string> =
        AVal.map (fun idx ->
            if idx >= 0 && idx < state.Items.Length then
                state.Items.[idx]
            else
                ""
        ) (CVal.value state.SelectedIndex)
    
    /// <summary>
    /// Key handler for select. Handles:
    /// - When closed: Space/Enter/Down opens the dropdown
    /// - When open: Up/Down navigates, Space/Enter selects, Escape closes
    /// </summary>
    /// <param name="state">The select state to modify.</param>
    /// <param name="event">The key event.</param>
    /// <returns>True if the event was handled.</returns>
    let selectHandler (state: SelectState) (event: Input.KeyEvent) : bool =
        let isOpen = AVal.getValue (CVal.value state.IsOpen)
        let focusedIdx = AVal.getValue (CVal.value state.FocusedIndex)
        let itemCount = state.Items.Length
        
        if isOpen then
            match event.Key with
            | ConsoleKey.UpArrow ->
                let newIdx = if focusedIdx > 0 then focusedIdx - 1 else itemCount - 1
                state.FocusedIndex.Set(newIdx)
                true
            | ConsoleKey.DownArrow ->
                let newIdx = if focusedIdx < itemCount - 1 then focusedIdx + 1 else 0
                state.FocusedIndex.Set(newIdx)
                true
            | ConsoleKey.Enter | ConsoleKey.Spacebar ->
                state.SelectedIndex.Set(focusedIdx)
                state.IsOpen.Set(false)
                true
            | ConsoleKey.Escape ->
                state.IsOpen.Set(false)
                true
            | _ -> false
        else
            match event.Key with
            | ConsoleKey.Enter | ConsoleKey.Spacebar | ConsoleKey.DownArrow ->
                state.IsOpen.Set(true)
                true
            | _ -> false
    
    // -------------------------------------------------------------------------
    // BUTTON
    // -------------------------------------------------------------------------
    
    /// <summary>
    /// State for a button widget.
    /// </summary>
    type ButtonState = {
        /// <summary>The button label.</summary>
        Label: string
        /// <summary>The action to execute when pressed.</summary>
        OnPress: ChangeableValue<unit -> unit>
    }
    
    /// <summary>
    /// Creates a new button state.
    /// </summary>
    /// <param name="label">The button label.</param>
    /// <param name="onPress">The action to execute when pressed.</param>
    /// <returns>A new ButtonState.</returns>
    let createButton (label: string) (onPress: unit -> unit) : ButtonState = {
        Label = label
        OnPress = CVal.create onPress
    }
    
    /// <summary>
    /// Creates a button widget from state.
    /// Displays as: ">[ Label ]&lt;" when focused.
    /// </summary>
    /// <param name="focused">Adaptive bool for focus state.</param>
    /// <param name="state">The button state.</param>
    /// <returns>A Widget rendering the button.</returns>
    let button (focused: IAdaptiveValue<bool>) (state: ButtonState) : Widget =
        let content =
            AVal.map (fun isFocused ->
                let prefix = if isFocused then ">" else " "
                let suffix = if isFocused then "<" else " "
                $"{prefix}[ {state.Label} ]{suffix}"
            ) focused
        { Content = content }
    
    /// <summary>
    /// Key handler for button. Triggers OnPress on Space or Enter.
    /// </summary>
    /// <param name="state">The button state.</param>
    /// <param name="event">The key event.</param>
    /// <returns>True if the event was handled.</returns>
    let buttonHandler (state: ButtonState) (event: Input.KeyEvent) : bool =
        match event.Key with
        | ConsoleKey.Enter | ConsoleKey.Spacebar ->
            let handler = AVal.getValue (CVal.value state.OnPress)
            handler ()
            true
        | _ -> false

// =============================================================================
// APP MODULE - Application loop infrastructure
// =============================================================================

/// <summary>
/// Application lifecycle management for TUI applications.
/// Provides the main loop, timing, and input handling infrastructure.
/// </summary>
/// <remarks>
/// <para>
/// <b>Basic usage:</b>
/// </para>
/// <code>
/// let state = App.createState()
/// let widget = Widget.constant "Hello!"
/// App.run App.defaultConfig state widget
/// </code>
/// <para>
/// <b>With input widgets:</b>
/// </para>
/// <code>
/// let state = App.createState()
/// let inputState = InputWidgets.createTextInput "Name" ""
/// let focusId = App.registerWidget "name" (InputWidgets.textInputHandler inputState) state
/// let focused = App.getFocused "name" state
/// let widget = InputWidgets.textInput 20 focused inputState
/// App.run App.defaultConfig state widget
/// </code>
/// </remarks>
module App =
    open System.Threading
    
    /// <summary>
    /// Configuration for the application loop.
    /// </summary>
    type AppConfig = {
        /// <summary>Target frames per second for rendering.</summary>
        FramesPerSecond: int
        /// <summary>Ticks per second for the Ticks counter.</summary>
        TicksPerSecond: int
    }
    
    /// <summary>
    /// Default configuration: 30 FPS, 60 ticks/second.
    /// </summary>
    let defaultConfig = {
        FramesPerSecond = 30
        TicksPerSecond = 60
    }
    
    /// <summary>
    /// Application state including running status, timing, and input.
    /// </summary>
    type AppState = {
        /// <summary>Set to false to stop the application loop.</summary>
        IsRunning: ChangeableValue<bool>
        /// <summary>Counter incremented at TicksPerSecond rate.</summary>
        Ticks: ChangeableValue<int64>
        /// <summary>Current window dimensions (auto-updated).</summary>
        WindowDimensions: WindowDimensions
        /// <summary>Input context for focus and key handling.</summary>
        InputContext: InputContext.Context
    }
    
    /// <summary>
    /// Creates a new application state.
    /// </summary>
    /// <returns>A fresh AppState ready for App.run.</returns>
    let createState () : AppState = {
        IsRunning = CVal.create true
        Ticks = CVal.create 0L
        WindowDimensions = WindowDimensions.create ()
        InputContext = InputContext.create ()
    }
    
    /// <summary>
    /// Stops the application by setting IsRunning to false.
    /// The main loop will exit on the next iteration.
    /// </summary>
    /// <param name="state">The application state to stop.</param>
    let stop (state: AppState) =
        state.IsRunning.Set(false)
    
    /// <summary>
    /// Runs the application main loop.
    /// Handles rendering, input, and timing until IsRunning becomes false.
    /// </summary>
    /// <param name="config">The application configuration.</param>
    /// <param name="state">The application state.</param>
    /// <param name="rootWidget">The root widget to render.</param>
    /// <remarks>
    /// <para>The main loop:</para>
    /// <list type="bullet">
    ///   <item><description>Hides the cursor</description></item>
    ///   <item><description>Starts tick and render timers</description></item>
    ///   <item><description>Polls for keyboard input</description></item>
    ///   <item><description>Dispatches input through InputContext</description></item>
    ///   <item><description>Restores cursor visibility on exit</description></item>
    /// </list>
    /// </remarks>
    let run (config: AppConfig) (state: AppState) (rootWidget: Widget) =
        Console.CursorVisible <- false
        try
            let renderer = Render.createRenderer ()
            let paddedContent =
                Render.padToWindow
                    (CVal.value state.WindowDimensions.Width)
                    (CVal.value state.WindowDimensions.Height)
                    rootWidget
            
            let frameDelay = max 1 (1000 / max 1 config.FramesPerSecond)
            let tickDelay = max 1 (1000 / max 1 config.TicksPerSecond)
            
            // Register focus navigation as global handler
            InputContext.registerGlobalHandler 
                (InputContext.handleFocusNavigation >> fun _ -> false) 
                state.InputContext |> ignore
            
            use _tickTimer = new Timer(TimerCallback(fun _ ->
                try
                    let next = (AVal.getValue (CVal.value state.Ticks)) + 1L
                    state.Ticks.Set(next)
                with _ -> ()
            ), null, 0, tickDelay)
            
            use _renderTimer = new Timer(TimerCallback(fun _ ->
                try
                    WindowDimensions.update state.WindowDimensions
                    renderer paddedContent
                with _ -> ()
            ), null, 0, frameDelay)
            
            while AVal.getValue (CVal.value state.IsRunning) do
                match Input.tryReadKey () with
                | Some event ->
                    // Handle Tab navigation specially
                    if not (InputContext.handleFocusNavigation event state.InputContext) then
                        InputContext.dispatch event state.InputContext |> ignore
                | None -> ()
                Thread.Sleep(10)
        finally
            Console.CursorVisible <- true
    
    /// <summary>
    /// Registers an interactive widget with the application.
    /// Adds the widget's key handler and registers it for focus.
    /// </summary>
    /// <param name="id">Unique string identifier for this widget.</param>
    /// <param name="handler">The widget's key handler function.</param>
    /// <param name="state">The application state.</param>
    /// <returns>The FocusId for use with Focus.setFocus.</returns>
    let registerWidget (id: string) (handler: Input.KeyHandler) (state: AppState) =
        let focusId = Focus.FocusId id
        InputContext.registerHandler focusId handler state.InputContext
        focusId
    
    /// <summary>
    /// Gets an adaptive bool indicating if a widget has focus.
    /// Use this when creating widgets that need to show focus state.
    /// </summary>
    /// <param name="id">The widget's string identifier.</param>
    /// <param name="state">The application state.</param>
    /// <returns>Adaptive bool that updates when focus changes.</returns>
    let getFocused (id: string) (state: AppState) : IAdaptiveValue<bool> =
        Focus.hasFocus (Focus.FocusId id) state.InputContext.FocusState
