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
        For Each box In {discipline, route, carClass, car}
            StyleChoice(box, parent)
        Next
        Field(parent, "Discipline", discipline)
        Field(parent, "Route", route)
        Field(parent, "Search cars", search)
        Field(parent, "Class", carClass)
        parent.Controls.Add(allCars)
        Field(parent, "Car", car)
        parent.Controls.Add(summary)
        pickerControls = {discipline, route, search, carClass, allCars, car}
        AddHandler normal.CheckedChanged, Sub() ModeChanged()
        AddHandler direct.CheckedChanged, Sub() ModeChanged()
        AddHandler discipline.SelectedIndexChanged, Sub()
                                                       If updating Then Return
                                                       FillRoutes(0) : FillCars("") : Edited()
                                                   End Sub
        AddHandler route.SelectedIndexChanged, Sub() If Not updating Then Edited()
        AddHandler search.TextChanged, Sub() If Not updating Then FillCars(SelectedCarCode()) : Edited()
        AddHandler carClass.SelectedIndexChanged, Sub() If Not updating Then FillCars(SelectedCarCode(), keepClass:=True) : Edited()
        AddHandler allCars.CheckedChanged, Sub() If Not updating Then FillCars(SelectedCarCode()) : Edited()
        AddHandler car.SelectedIndexChanged, Sub() If Not updating Then Edited()
        Apply("normal", 0, "")
    End Sub

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

    Public ReadOnly Property SummaryText As String
        Get
            Return summary.Text
        End Get
    End Property

    ' Shows saved choices; an unknown or missing route starts on the first rally stage.
    Public Sub Apply(launchMode As String, routeId As Integer, carCode As String)
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
        Edited()
    End Sub

    Private Sub Edited()
        If updating Then Return
        UpdateSummary()
        RaiseEvent Changed()
    End Sub

    Private Sub UpdateSummary()
        Dim chosenRoute = TryCast(route.SelectedItem, EventCatalog.EventRoute)
        Dim chosenCar = TryCast(car.SelectedItem, EventCatalog.EventCar)
        summary.Text = If(Not direct.Checked, "Launch and Launch VR open DiRT 3's own menus.",
                          If(chosenRoute Is Nothing OrElse chosenCar Is Nothing, "Choose a route and a car (adjust the search if no car is listed).",
                             "Direct practice: " & CurrentDisciplineName() & " · " & chosenRoute.ToString() & " · " & chosenCar.Name & ". No opponents; laps and the end of the session follow DiRT 3's benchmark start for now."))
    End Sub

    Private Function CurrentDisciplineName() As String
        Return If(TryCast(discipline.SelectedItem, EventCatalog.EventDiscipline)?.Name, "")
    End Function
End Class
