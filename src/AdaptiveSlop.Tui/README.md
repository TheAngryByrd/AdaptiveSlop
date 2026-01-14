# AdaptiveSlop.Tui

A reactive terminal UI library for F# built on adaptive values. Create declarative, automatically-updating console interfaces with minimal code.

## Table of Contents

- [Overview](#overview)
- [Architecture](#architecture)
- [Quick Start](#quick-start)
- [Core Concepts](#core-concepts)
- [Module Reference](#module-reference)
- [Input Widgets](#input-widgets)
- [Styling and Layout](#styling-and-layout)
- [Application Lifecycle](#application-lifecycle)
- [Examples](#examples)
- [LLM Integration Guide](#llm-integration-guide)

## Overview

AdaptiveSlop.Tui provides a reactive approach to building terminal UIs. Instead of manually redrawing the screen when data changes, you declare how your UI depends on data, and the framework handles updates automatically.

**Key Features:**
- **Reactive**: UI updates automatically when underlying data changes
- **Declarative**: Describe what to show, not how to update it
- **Composable**: Build complex UIs from simple, reusable widgets
- **Input Handling**: Built-in widgets for text input, checkboxes, radio buttons, etc.
- **Focus Management**: Tab navigation and keyboard event routing

## Architecture

```
┌─────────────────────────────────────────────────────────────────┐
│                        AdaptiveSlop.Tui                          │
├─────────────────────────────────────────────────────────────────┤
│  App Module           - Application lifecycle, main loop         │
│  ├── AppState         - Running state, ticks, dimensions         │
│  ├── AppConfig        - FPS, tick rate configuration             │
│  └── run              - Main application loop                    │
├─────────────────────────────────────────────────────────────────┤
│  InputWidgets Module  - Pre-built interactive widgets            │
│  ├── TextInput        - Text entry with cursor                   │
│  ├── Checkbox         - Boolean toggle                           │
│  ├── RadioGroup       - Single selection from list               │
│  ├── Select           - Dropdown selection                       │
│  └── Button           - Action trigger                           │
├─────────────────────────────────────────────────────────────────┤
│  InputContext Module  - Focus + key handler management           │
│  Focus Module         - Tab navigation, focus tracking           │
│  Input Module         - Key event types and reading              │
├─────────────────────────────────────────────────────────────────┤
│  Render Module        - Console output with change detection     │
│  WindowDimensions     - Reactive window size tracking            │
├─────────────────────────────────────────────────────────────────┤
│  Widget Module        - Core widget type and functions           │
│  View Module          - Declarative widget constructors          │
│  Layout Module        - Layout function aliases                  │
│  Prop Module          - Widget modifier functions                │
│  Text Module          - Text widget helpers                      │
├─────────────────────────────────────────────────────────────────┤
│                      AdaptiveSlop.Core                           │
│  (IAdaptiveValue, CVal, AVal - reactive primitives)              │
└─────────────────────────────────────────────────────────────────┘
```

## Quick Start

### Minimal Example

```fsharp
open AdaptiveSlop.Core
open AdaptiveSlop.Tui

[<EntryPoint>]
let main _ =
    let state = App.createState()
    let widget = Widget.constant "Hello, Terminal!"
    App.run App.defaultConfig state widget
    0
```

### Dynamic Content

```fsharp
let state = App.createState()

// Create reactive content using the tick counter
let tickDisplay = 
    AVal.map (sprintf "Ticks: %d") (CVal.value state.Ticks)
    |> Widget.text

App.run App.defaultConfig state tickDisplay
```

### With Input

```fsharp
let state = App.createState()

// Create text input
let nameInput = InputWidgets.createTextInput "Enter name..." ""
let focusId = App.registerWidget "name" (InputWidgets.textInputHandler nameInput) state
let focused = App.getFocused "name" state

// Create widget
let widget = InputWidgets.textInput 20 focused nameInput

// Add quit handler
InputContext.registerGlobalHandler (fun event ->
    if event.Key = ConsoleKey.Q then
        App.stop state
        true
    else false
) state.InputContext

App.run App.defaultConfig state widget
```

## Core Concepts

### Widget

The fundamental UI element. A Widget contains reactive string content:

```fsharp
type Widget = { Content: IAdaptiveValue<string> }
```

Widgets are:
- **Immutable** - Creating a modified widget returns a new widget
- **Reactive** - Content updates automatically when dependencies change
- **Composable** - Can be combined using layout functions

### Adaptive Values

From AdaptiveSlop.Core, these are the reactive primitives:

- `IAdaptiveValue<'T>` - Read-only reactive value
- `ChangeableValue<'T>` - Mutable reactive value (CVal)
- `AVal.map`, `AVal.map2`, etc. - Transform reactive values

```fsharp
// Mutable value
let counter = CVal.create 0

// Derived value (updates when counter changes)
let display = AVal.map (sprintf "Count: %d") (CVal.value counter)

// Widget automatically updates
let widget = Widget.text display

// Modify the source
counter.Set(counter.Value + 1) // Widget now shows "Count: 1"
```

### Focus System

Widgets can be focusable for keyboard input:

```fsharp
// 1. Create widget state
let inputState = InputWidgets.createTextInput "placeholder" ""

// 2. Register with App (adds to focus order)
let focusId = App.registerWidget "myInput" (InputWidgets.textInputHandler inputState) state

// 3. Get reactive focus state
let isFocused = App.getFocused "myInput" state

// 4. Create widget with focus state
let widget = InputWidgets.textInput 20 isFocused inputState
```

Tab navigation is automatic once widgets are registered.

## Module Reference

### Widget Module

Core widget creation and transformation:

| Function | Description |
|----------|-------------|
| `constant` | Static text widget |
| `text` | Widget from adaptive value |
| `map` | Transform content |
| `map2` | Combine two widgets |
| `vstack` | Stack vertically |
| `hstack` | Stack horizontally with separator |
| `border` | Add border characters |
| `padLeft/padRight/pad` | Add padding |
| `textbox` | Fixed-width constraint |
| `table` | ASCII table |
| `progressBar` | Progress indicator |
| `spinner` | Animated spinner |
| `keyValue` | "Key: Value" display |
| `responsiveRow` | Side-by-side or stacked |
| `sidebar` | Main + sidebar layout |

### View Module

Declarative constructors with modifier support:

```fsharp
// Basic
View.text [] "Hello"

// With modifiers
View.text [Prop.border '=' '|'] "Boxed"

// Layouts
View.vstack [Prop.border '-' '|'] [
    Widget.constant "Line 1"
    Widget.constant "Line 2"
]
```

### Input Module

Keyboard handling:

```fsharp
type KeyEvent = {
    Key: ConsoleKey
    KeyChar: char
    Modifiers: ConsoleModifiers
}

type KeyHandler = KeyEvent -> bool

// Non-blocking read
let key = Input.tryReadKey()

// Check if printable
if Input.isPrintable event then ...
```

### Focus Module

Focus management:

```fsharp
type FocusId = FocusId of string

// Create focus state
let focus = Focus.create()

// Register widget
Focus.register (FocusId "widget1") focus

// Set focus
Focus.setFocus (FocusId "widget1") focus

// Check focus (reactive)
let isFocused = Focus.hasFocus (FocusId "widget1") focus

// Navigation
Focus.focusNext focus  // Tab
Focus.focusPrev focus  // Shift+Tab
```

### InputContext Module

Combines focus with key handling:

```fsharp
let ctx = InputContext.create()

// Register widget handler
InputContext.registerHandler 
    (Focus.FocusId "input1") 
    (fun event -> (* handle and return bool *))
    ctx

// Register global handler (quit, help, etc.)
InputContext.registerGlobalHandler 
    (fun event -> 
        if event.Key = ConsoleKey.Q then true else false)
    ctx

// Dispatch event
InputContext.dispatch event ctx
```

### Render Module

Console output:

```fsharp
// Create renderer with change detection
let render = Render.createRenderer()

// Pad content to window size
let padded = Render.padToWindow width height widget

// In timer callback
render padded

// One-shot render
Render.renderOnce widget
```

### App Module

Application lifecycle:

```fsharp
// Configuration
type AppConfig = {
    FramesPerSecond: int   // Default: 30
    TicksPerSecond: int    // Default: 60
}

// State
type AppState = {
    IsRunning: ChangeableValue<bool>
    Ticks: ChangeableValue<int64>
    WindowDimensions: WindowDimensions
    InputContext: InputContext.Context
}

// Create and run
let state = App.createState()
App.run App.defaultConfig state myWidget

// Stop
App.stop state
```

## Input Widgets

### TextInput

Text entry with cursor support:

```fsharp
// State
type TextInputState = {
    Value: ChangeableValue<string>
    CursorPosition: ChangeableValue<int>
    Placeholder: string
}

// Create
let state = InputWidgets.createTextInput "Enter text..." "initial"

// Widget (needs focus state)
let widget = InputWidgets.textInput 30 focused state

// Handler (register with App)
let handler = InputWidgets.textInputHandler state

// Keys: Left/Right, Home/End, Backspace, Delete, printable chars
```

Display: `>[Hello_World]<` (when focused with cursor)

### Checkbox

Boolean toggle:

```fsharp
type CheckboxState = {
    Checked: ChangeableValue<bool>
    Label: string
}

let state = InputWidgets.createCheckbox "Enable feature" false
let widget = InputWidgets.checkbox focused state
let handler = InputWidgets.checkboxHandler state

// Keys: Space, Enter to toggle
```

Display: `>[X] Enable feature<` or `>[ ] Enable feature<`

### RadioGroup

Single selection from list:

```fsharp
type RadioItem = { Label: string; Value: string }

type RadioGroupState = {
    Items: RadioItem list
    SelectedIndex: ChangeableValue<int>
    FocusedIndex: ChangeableValue<int>
}

let items = [
    { Label = "Option A"; Value = "a" }
    { Label = "Option B"; Value = "b" }
]
let state = InputWidgets.createRadioGroup items 0
let widget = InputWidgets.radioGroup focused state
let handler = InputWidgets.radioGroupHandler state

// Get selected value
let selectedValue = InputWidgets.radioGroupValue state

// Keys: Up/Down to navigate, Space/Enter to select
```

Display:
```
>(o) Option A<
 ( ) Option B
```

### Select

Dropdown selection:

```fsharp
type SelectState = {
    Items: string list
    SelectedIndex: ChangeableValue<int>
    IsOpen: ChangeableValue<bool>
    FocusedIndex: ChangeableValue<int>
    MaxVisible: int
}

let state = InputWidgets.createSelect ["A"; "B"; "C"] 0 5
let widget = InputWidgets.select 20 focused state
let handler = InputWidgets.selectHandler state

// Get selected value
let selectedValue = InputWidgets.selectValue state

// Keys: Space/Enter/Down to open, Up/Down to navigate, 
//       Space/Enter to select, Escape to close
```

Display (closed): `>[A              v]<`  
Display (open): Shows dropdown list

### Button

Action trigger:

```fsharp
type ButtonState = {
    Label: string
    OnPress: ChangeableValue<unit -> unit>
}

let state = InputWidgets.createButton "Submit" (fun () -> 
    printfn "Submitted!")
let widget = InputWidgets.button focused state
let handler = InputWidgets.buttonHandler state

// Keys: Space, Enter to activate
```

Display: `>[ Submit ]<`

## Styling and Layout

### Borders

```fsharp
// Single border
Widget.constant "Content" |> Widget.border '-' '|'
// --------
// |Content|
// --------

// Double border
Widget.constant "Content" |> Widget.border '=' '|'
// ========
// |Content|
// ========
```

### Padding

```fsharp
Widget.constant "Hi" |> Widget.padLeft 10 ' '   // "        Hi"
Widget.constant "Hi" |> Widget.padRight 10 ' '  // "Hi        "
Widget.constant "Hi" |> Widget.pad 2 2 '-'      // "--Hi--"
```

### Layout

```fsharp
// Vertical stack
Widget.vstack [
    Widget.constant "Line 1"
    Widget.constant "Line 2"
]

// Horizontal with separator
Widget.hstack " | " [
    Widget.constant "A"
    Widget.constant "B"
]  // "A | B"

// Responsive (side-by-side or stacked based on width)
Widget.responsiveRow windowWidth 2 leftWidget rightWidget
```

### Tables

```fsharp
Widget.table 
    ["Name"; "Age"; "City"]
    [
        ["Alice"; "30"; "NYC"]
        ["Bob"; "25"; "LA"]
    ]
// Name  | Age | City
// ------+-----+-----
// Alice | 30  | NYC
// Bob   | 25  | LA
```

### Progress Bar

```fsharp
let progress = CVal.create 0.5
Widget.progressBar 20 (CVal.value progress)
// [██████████░░░░░░░░░░] 50%
```

### Spinner

```fsharp
let frames = ["⠋"; "⠙"; "⠹"; "⠸"; "⠼"; "⠴"; "⠦"; "⠧"; "⠇"; "⠏"]
let tick = CVal.create 0
Widget.spinner frames (CVal.value tick)
// Increment tick to animate
```

## Application Lifecycle

```fsharp
// 1. Create state
let state = App.createState()

// 2. Register widgets
let inputState = InputWidgets.createTextInput "" ""
let focusId = App.registerWidget "myInput" 
    (InputWidgets.textInputHandler inputState) state

// 3. Add global handlers (optional)
InputContext.registerGlobalHandler (fun event ->
    if event.Key = ConsoleKey.Escape then
        App.stop state
        true
    else false
) state.InputContext

// 4. Set initial focus (optional)
Focus.setFocus focusId state.InputContext.FocusState

// 5. Build UI
let focused = App.getFocused "myInput" state
let widget = InputWidgets.textInput 20 focused inputState

// 6. Run (blocks until stopped)
App.run App.defaultConfig state widget

// The loop handles:
// - Rendering at configured FPS
// - Incrementing Ticks at configured rate
// - Polling keyboard input
// - Dispatching to handlers
// - Window resize detection
```

## Examples

### Counter with Buttons

```fsharp
let state = App.createState()
let counter = CVal.create 0

// Increment button
let incButton = InputWidgets.createButton "+1" (fun () ->
    counter.Set(AVal.getValue (CVal.value counter) + 1))
let incId = App.registerWidget "inc" (InputWidgets.buttonHandler incButton) state
let incFocused = App.getFocused "inc" state

// Decrement button  
let decButton = InputWidgets.createButton "-1" (fun () ->
    counter.Set(AVal.getValue (CVal.value counter) - 1))
let decId = App.registerWidget "dec" (InputWidgets.buttonHandler decButton) state
let decFocused = App.getFocused "dec" state

// Display
let display = AVal.map (sprintf "Count: %d") (CVal.value counter)

// Layout
let widget = Widget.vstack [
    Widget.text display
    Widget.hstack "  " [
        InputWidgets.button incFocused incButton
        InputWidgets.button decFocused decButton
    ]
]

Focus.setFocus incId state.InputContext.FocusState
App.run App.defaultConfig state widget
```

### Form with Validation

```fsharp
let state = App.createState()

let nameInput = InputWidgets.createTextInput "Name (required)" ""
let emailInput = InputWidgets.createTextInput "Email" ""
let termsCheckbox = InputWidgets.createCheckbox "I accept terms" false

// Register all
let nameId = App.registerWidget "name" (InputWidgets.textInputHandler nameInput) state
let emailId = App.registerWidget "email" (InputWidgets.textInputHandler emailInput) state
let termsId = App.registerWidget "terms" (InputWidgets.checkboxHandler termsCheckbox) state

// Validation message (reactive)
let validation = 
    AVal.map3 (fun name email terms ->
        if String.IsNullOrEmpty(name) then "Name is required"
        elif not terms then "Please accept terms"
        else "Ready to submit!"
    ) (CVal.value nameInput.Value) 
      (CVal.value emailInput.Value) 
      (CVal.value termsCheckbox.Checked)

// Build form
let form = Widget.vstack [
    InputWidgets.textInput 30 (App.getFocused "name" state) nameInput
    InputWidgets.textInput 30 (App.getFocused "email" state) emailInput
    InputWidgets.checkbox (App.getFocused "terms" state) termsCheckbox
    Widget.constant ""
    Widget.text validation
] |> Widget.border '=' '|'

Focus.setFocus nameId state.InputContext.FocusState
App.run App.defaultConfig state form
```

## LLM Integration Guide

This section helps AI assistants understand how to work with AdaptiveSlop.Tui.

### Key Patterns

**Creating Static Content:**
```fsharp
Widget.constant "text"           // Simple text
Widget.table headers rows        // ASCII table
```

**Creating Dynamic Content:**
```fsharp
let value = CVal.create "initial"
Widget.text (CVal.value value)   // Reactive text
Widget.text (AVal.map f source)  // Transformed
```

**Layout Composition:**
```fsharp
Widget.vstack [w1; w2; w3]       // Vertical
Widget.hstack " " [w1; w2]       // Horizontal
w |> Widget.border '-' '|'       // Add border
```

**Input Widget Pattern:**
```fsharp
// 1. Create state
let state = InputWidgets.createTextInput placeholder initial

// 2. Register handler
let focusId = App.registerWidget "id" (InputWidgets.textInputHandler state) appState

// 3. Get focus
let focused = App.getFocused "id" appState

// 4. Create widget
let widget = InputWidgets.textInput width focused state
```

**Application Pattern:**
```fsharp
let state = App.createState()
// ... register widgets ...
// ... add global handlers ...
// ... build UI ...
App.run App.defaultConfig state rootWidget
```

### Common Tasks

**Add quit functionality:**
```fsharp
InputContext.registerGlobalHandler (fun event ->
    if event.Key = ConsoleKey.Q then
        App.stop state
        true
    else false
) state.InputContext
```

**Read input value:**
```fsharp
let currentValue = AVal.getValue (CVal.value inputState.Value)
```

**Update input programmatically:**
```fsharp
inputState.Value.Set("new value")
inputState.CursorPosition.Set(newValue.Length)
```

**Create responsive layout:**
```fsharp
Widget.responsiveRow (CVal.value state.WindowDimensions.Width) gap left right
```

### Type Reference (for LLMs)

```fsharp
// Core types
type Widget = { Content: IAdaptiveValue<string> }
type WidgetModifier = Widget -> Widget

// Input types
type KeyEvent = { Key: ConsoleKey; KeyChar: char; Modifiers: ConsoleModifiers }
type KeyHandler = KeyEvent -> bool

// Focus types
type FocusId = FocusId of string
type FocusState = { CurrentFocus: ChangeableValue<FocusId option>; FocusOrder: ChangeableValue<FocusId list> }

// App types
type AppConfig = { FramesPerSecond: int; TicksPerSecond: int }
type AppState = { IsRunning: ChangeableValue<bool>; Ticks: ChangeableValue<int64>; WindowDimensions: WindowDimensions; InputContext: InputContext.Context }

// Widget state types
type TextInputState = { Value: ChangeableValue<string>; CursorPosition: ChangeableValue<int>; Placeholder: string }
type CheckboxState = { Checked: ChangeableValue<bool>; Label: string }
type RadioItem = { Label: string; Value: string }
type RadioGroupState = { Items: RadioItem list; SelectedIndex: ChangeableValue<int>; FocusedIndex: ChangeableValue<int> }
type SelectState = { Items: string list; SelectedIndex: ChangeableValue<int>; IsOpen: ChangeableValue<bool>; FocusedIndex: ChangeableValue<int>; MaxVisible: int }
type ButtonState = { Label: string; OnPress: ChangeableValue<unit -> unit> }
```

### Module Function Signatures (for LLMs)

```fsharp
// Widget
Widget.constant : string -> Widget
Widget.text : IAdaptiveValue<string> -> Widget
Widget.map : (string -> string) -> Widget -> Widget
Widget.vstack : Widget list -> Widget
Widget.hstack : string -> Widget list -> Widget
Widget.border : char -> char -> Widget -> Widget
Widget.progressBar : int -> IAdaptiveValue<float> -> Widget
Widget.spinner : string list -> IAdaptiveValue<int> -> Widget

// InputWidgets
InputWidgets.createTextInput : string -> string -> TextInputState
InputWidgets.textInput : int -> IAdaptiveValue<bool> -> TextInputState -> Widget
InputWidgets.textInputHandler : TextInputState -> KeyHandler

InputWidgets.createCheckbox : string -> bool -> CheckboxState
InputWidgets.checkbox : IAdaptiveValue<bool> -> CheckboxState -> Widget
InputWidgets.checkboxHandler : CheckboxState -> KeyHandler

InputWidgets.createRadioGroup : RadioItem list -> int -> RadioGroupState
InputWidgets.radioGroup : IAdaptiveValue<bool> -> RadioGroupState -> Widget
InputWidgets.radioGroupHandler : RadioGroupState -> KeyHandler
InputWidgets.radioGroupValue : RadioGroupState -> IAdaptiveValue<string>

InputWidgets.createSelect : string list -> int -> int -> SelectState
InputWidgets.select : int -> IAdaptiveValue<bool> -> SelectState -> Widget
InputWidgets.selectHandler : SelectState -> KeyHandler
InputWidgets.selectValue : SelectState -> IAdaptiveValue<string>

InputWidgets.createButton : string -> (unit -> unit) -> ButtonState
InputWidgets.button : IAdaptiveValue<bool> -> ButtonState -> Widget
InputWidgets.buttonHandler : ButtonState -> KeyHandler

// App
App.createState : unit -> AppState
App.run : AppConfig -> AppState -> Widget -> unit
App.stop : AppState -> unit
App.registerWidget : string -> KeyHandler -> AppState -> FocusId
App.getFocused : string -> AppState -> IAdaptiveValue<bool>

// Focus
Focus.create : unit -> FocusState
Focus.setFocus : FocusId -> FocusState -> unit
Focus.hasFocus : FocusId -> FocusState -> IAdaptiveValue<bool>
Focus.focusNext : FocusState -> unit
Focus.focusPrev : FocusState -> unit

// InputContext
InputContext.create : unit -> Context
InputContext.registerHandler : FocusId -> KeyHandler -> Context -> unit
InputContext.registerGlobalHandler : KeyHandler -> Context -> unit
InputContext.dispatch : KeyEvent -> Context -> bool
```

---

## License

See repository root for license information.
