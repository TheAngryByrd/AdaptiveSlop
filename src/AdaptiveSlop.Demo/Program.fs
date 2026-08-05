open System
open AdaptiveSlop.Core
open AdaptiveSlop.Tui

// =============================================================================
// DEMO APPLICATION - Showcases all TUI widgets
// =============================================================================

[<EntryPoint>]
let main args =
    // Parse FPS from command line
    let fps =
        match args with
        | [| value |] ->
            match Int32.TryParse(value) with
            | true, parsed when parsed > 0 -> parsed
            | _ -> 30
        | _ -> 30

    let config =
        { App.defaultConfig with
            FramesPerSecond = fps }

    let state = App.createState ()

    // -------------------------------------------------------------------------
    // Create input widget states
    // -------------------------------------------------------------------------

    // Text input for name
    let nameInput = InputWidgets.createTextInput "Enter your name..." ""

    let nameFocusId =
        App.registerWidget "name" (InputWidgets.textInputHandler nameInput) state

    let nameFocused = App.getFocused "name" state

    // Text input for email
    let emailInput = InputWidgets.createTextInput "Enter email..." ""

    let _emailFocusId =
        App.registerWidget "email" (InputWidgets.textInputHandler emailInput) state

    let emailFocused = App.getFocused "email" state

    // Checkbox for newsletter
    let newsletterCheckbox = InputWidgets.createCheckbox "Subscribe to newsletter" false

    let _newsletterFocusId =
        App.registerWidget "newsletter" (InputWidgets.checkboxHandler newsletterCheckbox) state

    let newsletterFocused = App.getFocused "newsletter" state

    // Checkbox for terms
    let termsCheckbox = InputWidgets.createCheckbox "I agree to the terms" false

    let _termsFocusId =
        App.registerWidget "terms" (InputWidgets.checkboxHandler termsCheckbox) state

    let termsFocused = App.getFocused "terms" state

    // Radio group for theme
    let lightItem = { Label = "Light"; Value = "light" }: InputWidgets.RadioItem
    let darkItem = { Label = "Dark"; Value = "dark" }: InputWidgets.RadioItem
    let systemItem = { Label = "System"; Value = "system" }: InputWidgets.RadioItem
    let themeItems = [ lightItem; darkItem; systemItem ]
    let themeRadio = InputWidgets.createRadioGroup themeItems 1 // Default to Dark

    let _themeRadioFocusId =
        App.registerWidget "theme" (InputWidgets.radioGroupHandler themeRadio) state

    let themeRadioFocused = App.getFocused "theme" state

    // Select for country
    let countryItems =
        [ "United States"
          "Canada"
          "United Kingdom"
          "Germany"
          "France"
          "Japan"
          "Australia"
          "Brazil"
          "Other" ]

    let countrySelect = InputWidgets.createSelect countryItems 0 5 // Max 5 visible items

    let _countrySelectFocusId =
        App.registerWidget "country" (InputWidgets.selectHandler countrySelect) state

    let countrySelectFocused = App.getFocused "country" state

    // Submit button
    let mutable submitCount = 0

    let submitButton =
        InputWidgets.createButton "Submit" (fun () -> submitCount <- submitCount + 1)

    let _submitFocusId =
        App.registerWidget "submit" (InputWidgets.buttonHandler submitButton) state

    let submitFocused = App.getFocused "submit" state

    // Reset button
    let resetButton =
        InputWidgets.createButton "Reset" (fun () ->
            nameInput.Value.Set("")
            nameInput.CursorPosition.Set(0)
            emailInput.Value.Set("")
            emailInput.CursorPosition.Set(0)
            newsletterCheckbox.Checked.Set(false)
            termsCheckbox.Checked.Set(false)
            themeRadio.SelectedIndex.Set(1)
            themeRadio.FocusedIndex.Set(1)
            countrySelect.SelectedIndex.Set(0)
            countrySelect.FocusedIndex.Set(0))

    let _resetFocusId =
        App.registerWidget "reset" (InputWidgets.buttonHandler resetButton) state

    let resetFocused = App.getFocused "reset" state

    // Quit button
    let quitButton = InputWidgets.createButton "Quit" (fun () -> App.stop state)

    let _quitFocusId =
        App.registerWidget "quit" (InputWidgets.buttonHandler quitButton) state

    let quitFocused = App.getFocused "quit" state

    // Register global Q key to quit
    InputContext.registerGlobalHandler
        (fun event ->
            if event.Key = ConsoleKey.Q && event.Modifiers = ConsoleModifiers.None then
                App.stop state
                true
            else
                false)
        state.InputContext

    // Set initial focus
    Focus.setFocus nameFocusId state.InputContext.FocusState

    // -------------------------------------------------------------------------
    // Build UI
    // -------------------------------------------------------------------------

    // Header with spinner
    let spinnerFrames = [ "⠋"; "⠙"; "⠹"; "⠸"; "⠼"; "⠴"; "⠦"; "⠧"; "⠇"; "⠏" ]
    let spinnerIndex = AVal.map (fun t -> int (t % 10L)) (CVal.value state.Ticks)
    let spinner = View.spinner [] spinnerFrames spinnerIndex
    let title = Widget.constant "AdaptiveSlop TUI Demo"
    let header = View.hstack [] " " [ spinner; title; spinner ]

    // Subtitle
    let subtitle =
        Widget.constant "Use Tab to navigate, Enter/Space to interact, Q to quit"

    // Form section header
    let formHeader = Widget.constant "=== User Registration Form ==="

    // Name field
    let nameLabel = Widget.constant "Name:"
    let nameWidget = InputWidgets.textInput 30 nameFocused nameInput
    let nameRow = View.hstack [] " " [ nameLabel; nameWidget ]

    // Email field
    let emailLabel = Widget.constant "Email:"
    let emailWidget = InputWidgets.textInput 30 emailFocused emailInput
    let emailRow = View.hstack [] " " [ emailLabel; emailWidget ]

    // Checkboxes
    let checkboxHeader = Widget.constant "Options:"
    let newsletterWidget = InputWidgets.checkbox newsletterFocused newsletterCheckbox
    let termsWidget = InputWidgets.checkbox termsFocused termsCheckbox
    let checkboxList = [ checkboxHeader; newsletterWidget; termsWidget ]
    let checkboxSection = View.vstack [] checkboxList

    // Radio group
    let themeHeader = Widget.constant "Theme:"
    let themeWidget = InputWidgets.radioGroup themeRadioFocused themeRadio
    let themeList = [ themeHeader; themeWidget ]
    let themeSection = View.vstack [] themeList

    // Country select
    let countryLabel = Widget.constant "Country:"
    let countryWidget = InputWidgets.select 20 countrySelectFocused countrySelect
    let countryList = [ countryLabel; countryWidget ]
    let countrySection = View.vstack [] countryList

    // Buttons
    let submitWidget = InputWidgets.button submitFocused submitButton
    let resetWidget = InputWidgets.button resetFocused resetButton
    let quitWidget = InputWidgets.button quitFocused quitButton
    let buttonRow = View.hstack [] "  " [ submitWidget; resetWidget; quitWidget ]

    // Status panel - shows current form values
    let statusContent =
        AVal.map4
            (fun name email newsletter terms ->
                let nameDisplay = if String.IsNullOrEmpty(name) then "(empty)" else name
                let emailDisplay = if String.IsNullOrEmpty(email) then "(empty)" else email
                let newsletterDisplay = if newsletter then "Yes" else "No"
                let termsDisplay = if terms then "Yes" else "No"

                let lines =
                    [ "--- Current Values ---"
                      sprintf "Name: %s" nameDisplay
                      sprintf "Email: %s" emailDisplay
                      sprintf "Newsletter: %s" newsletterDisplay
                      sprintf "Terms Accepted: %s" termsDisplay ]

                String.concat Environment.NewLine lines)
            (CVal.value nameInput.Value)
            (CVal.value emailInput.Value)
            (CVal.value newsletterCheckbox.Checked)
            (CVal.value termsCheckbox.Checked)

    let statusContent2 =
        AVal.map3
            (fun theme country ticks ->
                let lines =
                    [ sprintf "Theme: %s" theme
                      sprintf "Country: %s" country
                      sprintf "Ticks: %d" ticks ]

                String.concat Environment.NewLine lines)
            (InputWidgets.radioGroupValue themeRadio)
            (InputWidgets.selectValue countrySelect)
            (CVal.value state.Ticks)

    let fullStatus =
        AVal.map2 (fun s1 s2 -> s1 + Environment.NewLine + s2) statusContent statusContent2

    let statusWidget = fullStatus |> Widget.text |> Widget.border '-' '|'

    // Progress bar showing ticks
    let progressValue =
        AVal.map (fun t -> float (t % 100L) / 100.0) (CVal.value state.Ticks)

    let progressBar = View.progressBar [] 30 progressValue

    // Main form layout
    let formWidgets =
        [ formHeader
          Widget.constant ""
          nameRow
          emailRow
          Widget.constant ""
          checkboxSection
          Widget.constant ""
          themeSection
          Widget.constant ""
          countrySection
          Widget.constant ""
          buttonRow ]

    let formContent = formWidgets |> View.vstack [] |> Widget.border '=' '|'

    // Side-by-side layout with status
    let mainContent =
        View.responsiveRow [] (CVal.value state.WindowDimensions.Width) 4 formContent statusWidget

    // Full layout
    let fullLayoutWidgets =
        [ header
          subtitle
          Widget.constant ""
          mainContent
          Widget.constant ""
          progressBar
          Widget.constant ""
          Widget.constant "Press Q to quit at any time" ]

    let fullLayout = View.vstack [] fullLayoutWidgets

    // Run the application
    App.run config state fullLayout

    0
