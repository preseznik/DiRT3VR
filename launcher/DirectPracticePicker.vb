Imports System.Windows.Forms

' Launch mode on the Launcher tab: Normal launch (DiRT 3's menus) or Direct practice, with a picker for
' discipline, route and car (adapted from DiRT2VR's DirectPractice and GRID2VR's picker: car search
' by name, code and class, a class filter and a summary line). Cars follow the route's discipline
' unless the player asks for all of them; other combinations are untested.
Public NotInheritable Class DirectPracticePicker
    Private ReadOnly normal As New RadioButton With {.Name = "LaunchModeNormal", .Text = "Normal launch: choose career or event in DiRT 3's menus", .AutoSize = True, .Checked = True}
    Private ReadOnly direct As New RadioButton With {.Name = "LaunchModeDirect", .Text = "Direct practice: start straight on a chosen route", .AutoSize = True}
    Private ReadOnly discipline As New ComboBox With {.Name = "DirectDiscipline", .DropDownStyle = ComboBoxStyle.DropDownList}
    Private ReadOnly route As New ComboBox With {.Name = "DirectRoute", .DropDownStyle = ComboBoxStyle.DropDownList}
    Private ReadOnly search As New TextBox With {.Name = "DirectCarSearch", .AccessibleName = "Search cars"}
    Private ReadOnly carClass As New ComboBox With {.Name = "DirectCarClass", .DropDownStyle = ComboBoxStyle.DropDownList}
    Private ReadOnly allCars As New CheckBox With {.Name = "DirectAllCars", .Text = "Show cars from other disciplines (untested)", .AutoSize = True}
    Private ReadOnly car As New ComboBox With {.Name = "DirectCar", .DropDownStyle = ComboBoxStyle.DropDownList}
    Private ReadOnly laps As New ValueSlider("DirectLaps", 1, 5, 1, valueLabels:=Counted(1, 5, "lap", "laps"))
    Private ReadOnly opponents As New ValueSlider("DirectOpponents", 0, DirectPractice.MaxOpponents, 0, valueLabels:=Counted(0, DirectPractice.MaxOpponents, "opponent", "opponents"))
    Private ReadOnly rivalCars As New ComboBox With {.Name = "DirectOpponentCars", .DropDownStyle = ComboBoxStyle.DropDownList}
    Private ReadOnly aiLevel As New ComboBox With {.Name = "DirectDifficulty", .DropDownStyle = ComboBoxStyle.DropDownList}
    Private ReadOnly finish As New ComboBox With {.Name = "DirectAfterFinish", .DropDownStyle = ComboBoxStyle.DropDownList}
    ' Assists and gearbox: a direct start uses DiRT 3's defaults, not the player's profile.
    Private ReadOnly gears As New ComboBox With {.Name = "DirectGearbox", .DropDownStyle = ComboBoxStyle.DropDownList}
    Private ReadOnly abs As New CheckBox With {.Name = "DirectAbs", .Text = "ABS", .AutoSize = True, .Checked = True}
    Private ReadOnly stability As New CheckBox With {.Name = "DirectStability", .Text = "Stability control", .AutoSize = True}
    Private ReadOnly cornerBraking As New CheckBox With {.Name = "DirectCornerBraking", .Text = "Corner braking", .AutoSize = True}
    Private ReadOnly throttle As New CheckBox With {.Name = "DirectThrottle", .Text = "Throttle management", .AutoSize = True}
    Private ReadOnly steering As New CheckBox With {.Name = "DirectSteering", .Text = "Steering assist", .AutoSize = True}
    Private ReadOnly summary As New Label With {.Name = "DirectSummary", .AutoSize = True, .UseMnemonic = False, .Margin = New Padding(0, 6, 0, 10)}
    Private ReadOnly pickerControls As Control()
    Private updating As Boolean
    Public Event Changed()

    Public Sub New(parent As Control)
        Dim catalog = EventCatalog.Current
        parent.Controls.Add(normal) : parent.Controls.Add(direct)
        For Each item In catalog.Disciplines
            discipline.Items.Add(item)
        Next
        rivalCars.Items.AddRange({"Same class as my car", "Mixed: any car of this discipline", "My car's model"})
        aiLevel.Items.AddRange({"DiRT 3's default", "Level 0 (easiest)", "Level 1", "Level 2", "Level 3", "Level 4", "Level 5 (hardest)"})
        finish.Items.AddRange({"Quit to desktop", "Return to DiRT 3's menus"})
        gears.Items.AddRange({"Automatic (DiRT 3's default)", "Manual: sequential", "Manual: H-pattern", "Manual: H-pattern with clutch"})
        For Each box In {discipline, route, carClass, car, rivalCars, aiLevel, finish, gears}
            StyleChoice(box, parent)
        Next
        Field(parent, "Discipline", discipline)
        Field(parent, "Route", route)
        Field(parent, "Search cars", search)
        Field(parent, "Class", carClass)
        parent.Controls.Add(allCars)
        Field(parent, "Car", car)
        Field(parent, "Laps (circuits)", laps)
        Field(parent, "Opponents (circuits)", opponents)
        Field(parent, "Opponent cars", rivalCars)
        Field(parent, "Difficulty", aiLevel)
        Field(parent, "After the finish", finish)
        Field(parent, "Gearbox", gears)
        Dim assistBoxes As New FlowLayoutPanel With {.Name = "DirectAssists", .AutoSize = True, .WrapContents = True, .Margin = New Padding(0)}
        assistBoxes.Controls.AddRange({abs, stability, cornerBraking, throttle, steering})
        Field(parent, "Assists", assistBoxes)
        parent.Controls.Add(summary)
        pickerControls = {discipline, route, search, carClass, allCars, car, finish, gears, abs, stability, cornerBraking, throttle, steering}
        AddHandler normal.CheckedChanged, Sub() ModeChanged()
        AddHandler direct.CheckedChanged, Sub() ModeChanged()
        AddHandler discipline.SelectedIndexChanged, Sub()
                                                       If updating Then Return
                                                       FillRoutes(0) : FillCars("") : Edited()
                                                   End Sub
        AddHandler route.SelectedIndexChanged, Sub()
                                                   UpdateRaceControls()
                                                   If Not updating Then Edited()
                                               End Sub
        AddHandler laps.ValueChanged, Sub() If Not updating Then Edited()
        AddHandler opponents.ValueChanged, Sub()
                                               UpdateRaceControls()
                                               If Not updating Then Edited()
                                           End Sub
        AddHandler rivalCars.SelectedIndexChanged, Sub() If Not updating Then Edited()
        AddHandler aiLevel.SelectedIndexChanged, Sub() If Not updating Then Edited()
        AddHandler finish.SelectedIndexChanged, Sub() If Not updating Then Edited()
        AddHandler gears.SelectedIndexChanged, Sub() If Not updating Then Edited()
        For Each box In {abs, stability, cornerBraking, throttle, steering}
            AddHandler box.CheckedChanged, Sub() If Not updating Then Edited()
        Next
        AddHandler search.TextChanged, Sub() If Not updating Then FillCars(SelectedCarCode()) : Edited()
        AddHandler carClass.SelectedIndexChanged, Sub() If Not updating Then FillCars(SelectedCarCode(), keepClass:=True) : Edited()
        AddHandler allCars.CheckedChanged, Sub() If Not updating Then FillCars(SelectedCarCode()) : Edited()
        AddHandler car.SelectedIndexChanged, Sub() If Not updating Then Edited()
        Apply("normal", 0, "", 1, 0, "class", -1, "quit")
        ApplyAssists("automatic", True, False, False, False, False)
    End Sub

    ' Shows saved assists; DiRT 3's defaults are ABS on, the rest off, automatic gears.
    Public Sub ApplyAssists(gearboxChoice As String, absOn As Boolean, stabilityOn As Boolean, cornerBrakingOn As Boolean, throttleOn As Boolean, steeringOn As Boolean)
        updating = True
        Try
            gears.SelectedIndex = Math.Max(0, Array.IndexOf(DirectPractice.GearboxChoices, gearboxChoice))
            abs.Checked = absOn : stability.Checked = stabilityOn : cornerBraking.Checked = cornerBrakingOn : throttle.Checked = throttleOn : steering.Checked = steeringOn
        Finally
            updating = False
        End Try
        UpdateSummary()
    End Sub

    Public ReadOnly Property Gearbox As String
        Get
            Return DirectPractice.GearboxChoices(Math.Max(0, gears.SelectedIndex))
        End Get
    End Property

    Public ReadOnly Property AssistStates As (Abs As Boolean, Stability As Boolean, CornerBraking As Boolean, Throttle As Boolean, Steering As Boolean)
        Get
            Return (abs.Checked, stability.Checked, cornerBraking.Checked, throttle.Checked, steering.Checked)
        End Get
    End Property

    ' "None", "1 lap", "2 laps"… for the sliders.
    Private Shared Function Counted(low As Integer, high As Integer, one As String, many As String) As String()
        Return Enumerable.Range(low, high - low + 1).Select(Function(value) If(value = 0, "None", value & " " & If(value = 1, one, many))).ToArray()
    End Function

    Public ReadOnly Property Mode As String
        Get
            Return If(direct.Checked, "direct", "normal")
        End Get
    End Property

    Public ReadOnly Property RouteId As Integer
        Get
            Return If(TryCast(route.SelectedItem, EventCatalog.EventRoute)?.Id, 0)
        End Get
    End Property

    Public ReadOnly Property CarCode As String
        Get
            Return SelectedCarCode()
        End Get
    End Property

    Public ReadOnly Property LapCount As Integer
        Get
            Return laps.Value
        End Get
    End Property

    Public ReadOnly Property OpponentCount As Integer
        Get
            Return opponents.Value
        End Get
    End Property

    Public ReadOnly Property OpponentCars As String
        Get
            Return DirectPractice.OpponentCarChoices(Math.Max(0, rivalCars.SelectedIndex))
        End Get
    End Property

    ' -1 = DiRT 3's default level, 0 to 5 = the AI level for this session.
    Public ReadOnly Property DifficultyLevel As Integer
        Get
            Return Math.Max(0, aiLevel.SelectedIndex) - 1
        End Get
    End Property

    Public ReadOnly Property AfterFinish As String
        Get
            Return DirectPractice.AfterFinishChoices(Math.Max(0, finish.SelectedIndex))
        End Get
    End Property

    Public ReadOnly Property SummaryText As String
        Get
            Return summary.Text
        End Get
    End Property

    ' Shows saved choices; an unknown or missing route starts on the first rally stage.
    Public Sub Apply(launchMode As String, routeId As Integer, carCode As String, lapCount As Integer, opponentCount As Integer, opponentChoice As String, level As Integer, afterFinishChoice As String)
        Dim catalog = EventCatalog.Current
        updating = True
        Try
            direct.Checked = launchMode = "direct" : normal.Checked = Not direct.Checked
            Dim chosen = catalog.FindRoute(routeId)
            discipline.SelectedItem = If(chosen IsNot Nothing, catalog.FindDiscipline(chosen.Discipline), catalog.Disciplines(0))
            FillRoutes(routeId)
            search.Text = ""
            Dim chosenCar = catalog.FindCar(carCode)
            allCars.Checked = chosenCar IsNot Nothing AndAlso chosenCar.Discipline <> CurrentDiscipline()
            FillCars(carCode)
            laps.Value = Math.Clamp(lapCount, 1, 5)
            opponents.Value = Math.Clamp(opponentCount, 0, DirectPractice.MaxOpponents)
            rivalCars.SelectedIndex = Math.Max(0, Array.IndexOf(DirectPractice.OpponentCarChoices, opponentChoice))
            aiLevel.SelectedIndex = Math.Clamp(level, -1, DirectPractice.MaxDifficulty) + 1
            finish.SelectedIndex = Math.Max(0, Array.IndexOf(DirectPractice.AfterFinishChoices, afterFinishChoice))
        Finally
            updating = False
        End Try
        ModeChanged()
    End Sub

    Private Function CurrentDiscipline() As String
        Return If(TryCast(discipline.SelectedItem, EventCatalog.EventDiscipline)?.Id, "")
    End Function

    Private Function SelectedCarCode() As String
        Return If(TryCast(car.SelectedItem, EventCatalog.EventCar)?.Code, "")
    End Function

    Private Sub FillRoutes(routeId As Integer)
        Dim wasUpdating = updating
        updating = True
        Try
            route.Items.Clear()
            For Each item In EventCatalog.Current.RoutesFor(CurrentDiscipline())
                route.Items.Add(item)
                If item.Id = routeId Then route.SelectedItem = item
            Next
            If route.SelectedIndex < 0 AndAlso route.Items.Count > 0 Then route.SelectedIndex = 0
        Finally
            updating = wasUpdating
        End Try
    End Sub

    ' Refills classes and cars for the current filters, keeping the chosen car when it still matches.
    Private Sub FillCars(keepCode As String, Optional keepClass As Boolean = False)
        Dim catalog = EventCatalog.Current
        Dim wasUpdating = updating
        updating = True
        Try
            Dim chosenClass = If(carClass.SelectedIndex > 0, carClass.SelectedItem.ToString(), "")
            If Not keepClass Then
                carClass.Items.Clear()
                carClass.Items.Add("All classes")
                For Each name In catalog.CarsFor(CurrentDiscipline(), allCars.Checked, "", "").Select(Function(item) item.VehicleClassName).Distinct()
                    carClass.Items.Add(name)
                Next
                carClass.SelectedIndex = Math.Max(0, carClass.Items.IndexOf(chosenClass))
                chosenClass = If(carClass.SelectedIndex > 0, carClass.SelectedItem.ToString(), "")
            End If
            car.Items.Clear()
            For Each item In catalog.CarsFor(CurrentDiscipline(), allCars.Checked, chosenClass, search.Text)
                car.Items.Add(item)
                If item.Code = keepCode Then car.SelectedItem = item
            Next
            If car.SelectedIndex < 0 AndAlso car.Items.Count > 0 Then car.SelectedIndex = 0
        Finally
            updating = wasUpdating
        End Try
    End Sub

    Private Sub ModeChanged()
        For Each item In pickerControls
            item.Enabled = direct.Checked
        Next
        UpdateRaceControls()
        Edited()
    End Sub

    ' Laps and opponents apply to circuits; point-to-point stages are one run, alone.
    Private Sub UpdateRaceControls()
        Dim circuit = direct.Checked AndAlso String.Equals(TryCast(route.SelectedItem, EventCatalog.EventRoute)?.TrackType, "circuit", StringComparison.Ordinal)
        laps.Enabled = circuit : opponents.Enabled = circuit
        rivalCars.Enabled = circuit AndAlso opponents.Value > 0
        aiLevel.Enabled = rivalCars.Enabled
    End Sub

    Private Sub Edited()
        If updating Then Return
        UpdateSummary()
        RaiseEvent Changed()
    End Sub

    Private Sub UpdateSummary()
        Dim chosenRoute = TryCast(route.SelectedItem, EventCatalog.EventRoute)
        Dim chosenCar = TryCast(car.SelectedItem, EventCatalog.EventCar)
        Dim circuit = chosenRoute IsNot Nothing AndAlso chosenRoute.TrackType = "circuit"
        Dim rivals = If(circuit, opponents.Value, 0)
        summary.Text = If(Not direct.Checked, "Launch and Launch VR open DiRT 3's own menus.",
                          If(chosenRoute Is Nothing OrElse chosenCar Is Nothing, "Choose a route and a car (adjust the search if no car is listed).",
                             "Direct practice: " & CurrentDisciplineName() & " · " & chosenRoute.ToString() & If(circuit, " · " & laps.Value & If(laps.Value = 1, " lap", " laps"), "") &
                             If(rivals > 0, " · " & rivals & If(rivals = 1, " opponent", " opponents") & " (" & {"same class", "mixed cars", "your car's model"}(Math.Max(0, rivalCars.SelectedIndex)) & If(DifficultyLevel >= 0, ", AI level " & DifficultyLevel, "") & ")", "") &
                             " · " & chosenCar.Name & {"", " · sequential gears", " · H-pattern gears", " · H-pattern gears with clutch"}(Math.Max(0, gears.SelectedIndex)) & ". " & If(rivals > 0, "", "No opponents. ") & If(AfterFinish = "menus", "After the finish DiRT 3 returns to its menus.", "DiRT 3 closes by itself after the finish.")))
    End Sub

    Private Function CurrentDisciplineName() As String
        Return If(TryCast(discipline.SelectedItem, EventCatalog.EventDiscipline)?.Name, "")
    End Function
End Class
