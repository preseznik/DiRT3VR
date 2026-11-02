Imports System.Windows.Forms

' Controls → Seat (VR): per-car seat offsets in centimetres (#2fdaf181; adapted from GRID2VR's per-car
' seat alignment and DiRT2VR's "same seat for every car"). Pick a car, move the eye point sideways, up
' or forward, reset it, or use one position for every car. Launch VR applies it (SeatPositions).
Public NotInheritable Class SeatPicker
    Private ReadOnly car As New ComboBox With {.Name = "SeatCar", .DropDownStyle = ComboBoxStyle.DropDownList}
    Private ReadOnly everyCar As New CheckBox With {.Name = "SeatSameForAll", .Text = "Same position for all cars", .AutoSize = True}
    Private ReadOnly sideways As New ValueSlider("SeatSideways", -SeatOffset.Limit, SeatOffset.Limit, 0, valueLabels:=Labels("left", "right"))
    Private ReadOnly up As New ValueSlider("SeatHeight", -SeatOffset.Limit, SeatOffset.Limit, 0, valueLabels:=Labels("up", "down"))
    Private ReadOnly forward As New ValueSlider("SeatForward", -SeatOffset.Limit, SeatOffset.Limit, 0, valueLabels:=Labels("forward", "back"))
    Private ReadOnly reset As New Button With {.Name = "SeatReset", .Text = "Reset this car", .AutoSize = True}
    Private ReadOnly summary As New Label With {.Name = "SeatSummary", .AutoSize = True, .UseMnemonic = False, .Margin = New Padding(0, 6, 0, 10)}
    Private ReadOnly cockpitStart As New CheckBox With {.Name = "SeatStartInCockpit", .Text = "Cockpit view only: start races in the cockpit", .AutoSize = True}
    Private ReadOnly cockpitNote As New Label With {.Name = "SeatStartInCockpitNote", .AutoSize = True, .UseMnemonic = False, .Margin = New Padding(0, 4, 0, 12),
        .Text = "DiRT 3 starts a race with the first view in each car's camera list: chase close, chase far, bumper, bonnet, then cockpit. This hides those four for the VR session, so races start in the cockpit and Change View stays there. Launch VR only."}
    Private ReadOnly perCar As New Dictionary(Of String, SeatOffset)(StringComparer.Ordinal)
    Private everyCarOffset As New SeatOffset
    Private updating As Boolean
    Public Event Changed()

    ' A car in the list, marked when it has its own seat position.
    Private NotInheritable Class CarItem
        Public ReadOnly Car As EventCatalog.EventCar
        Private ReadOnly owner As SeatPicker
        Public Sub New(value As EventCatalog.EventCar, picker As SeatPicker)
            Car = value : owner = picker
        End Sub
        Public Overrides Function ToString() As String
            Return Car.ToString() & If(owner.perCar.ContainsKey(Car.Code), " · adjusted", "")
        End Function
    End Class

    Public Sub New(parent As Control)
        For Each item In EventCatalog.Current.Cars.OrderBy(Function(value) value.Name, StringComparer.Ordinal)
            car.Items.Add(New CarItem(item, Me))
        Next
        StyleChoice(car, parent)
        Field(parent, "Car", car)
        parent.Controls.Add(everyCar)
        Field(parent, "Sideways", sideways)
        Field(parent, "Height", up)
        Field(parent, "Forward", forward)
        parent.Controls.Add(reset)
        parent.Controls.Add(summary)
        parent.Controls.Add(cockpitStart)
        parent.Controls.Add(cockpitNote)
        AddHandler cockpitStart.CheckedChanged, Sub() If Not updating Then RaiseEvent Changed()
        AddHandler car.SelectedIndexChanged, Sub() ShowCurrent()
        AddHandler everyCar.CheckedChanged, Sub()
                                                 If updating Then Return
                                                 ShowCurrent() : Edited()
                                             End Sub
        For Each slider In {sideways, up, forward}
            AddHandler slider.ValueChanged, Sub() If Not updating Then Store()
        Next
        AddHandler reset.Click, Sub()
                                    updating = True
                                    Try
                                        sideways.Value = 0 : up.Value = 0 : forward.Value = 0
                                    Finally
                                        updating = False
                                    End Try
                                    Store()
                                End Sub
        Apply(Nothing, False, Nothing, "", False)
    End Sub

    ' "12 cm left" … "0 cm" … "12 cm right" for -50..50.
    Private Shared Function Labels(positive As String, negative As String) As String()
        Return Enumerable.Range(-SeatOffset.Limit, 2 * SeatOffset.Limit + 1).Select(Function(value) If(value = 0, "0 cm", Math.Abs(value) & " cm " & If(value > 0, positive, negative))).ToArray()
    End Function

    ' The cars with their own offset, in name order of their codes (zero offsets are dropped).
    Public ReadOnly Property Offsets As Dictionary(Of String, SeatOffset)
        Get
            Return perCar.OrderBy(Function(pair) pair.Key, StringComparer.Ordinal).ToDictionary(Function(pair) pair.Key, Function(pair) pair.Value.Copy(), StringComparer.Ordinal)
        End Get
    End Property

    Public ReadOnly Property SameForAll As Boolean
        Get
            Return everyCar.Checked
        End Get
    End Property

    Public ReadOnly Property AllCars As SeatOffset
        Get
            Return everyCarOffset.Copy()
        End Get
    End Property

    Public ReadOnly Property SelectedCar As String
        Get
            Return If(TryCast(car.SelectedItem, CarItem)?.Car.Code, "")
        End Get
    End Property

    Public ReadOnly Property StartInCockpit As Boolean
        Get
            Return cockpitStart.Checked
        End Get
    End Property

    Public ReadOnly Property SummaryText As String
        Get
            Return summary.Text
        End Get
    End Property

    ' Shows saved positions; the list opens on preferredCar (for example the direct practice car).
    Public Sub Apply(offsets As IDictionary(Of String, SeatOffset), sameForAll As Boolean, allCarsOffset As SeatOffset, preferredCar As String, cockpit As Boolean)
        updating = True
        Try
            cockpitStart.Checked = cockpit
            perCar.Clear()
            For Each pair In If(offsets, New Dictionary(Of String, SeatOffset))
                If pair.Value IsNot Nothing AndAlso Not pair.Value.IsZero Then perCar(pair.Key) = pair.Value.Copy()
            Next
            everyCarOffset = If(allCarsOffset, New SeatOffset).Copy()
            everyCar.Checked = sameForAll
            Dim preferred = car.Items.Cast(Of CarItem)().FirstOrDefault(Function(item) item.Car.Code = preferredCar)
            If preferred IsNot Nothing Then
                car.SelectedItem = preferred
            ElseIf car.SelectedIndex < 0 AndAlso car.Items.Count > 0 Then
                car.SelectedIndex = 0
            End If
        Finally
            updating = False
        End Try
        ShowCurrent()
    End Sub

    Private Function Current() As SeatOffset
        If everyCar.Checked Then Return everyCarOffset
        Dim offset As SeatOffset = Nothing
        Return If(perCar.TryGetValue(SelectedCar, offset), offset, New SeatOffset)
    End Function

    Private Sub ShowCurrent()
        Dim wasUpdating = updating
        updating = True
        Try
            Dim offset = Current()
            sideways.Value = offset.Sideways : up.Value = offset.Up : forward.Value = offset.Forward
            car.Enabled = Not everyCar.Checked
            reset.Text = If(everyCar.Checked, "Reset", "Reset this car")
        Finally
            updating = wasUpdating
        End Try
        UpdateSummary()
    End Sub

    Private Sub Store()
        Dim offset As New SeatOffset With {.Sideways = sideways.Value, .Up = up.Value, .Forward = forward.Value}
        If everyCar.Checked Then
            everyCarOffset = offset
        ElseIf SelectedCar <> "" Then
            If offset.IsZero Then perCar.Remove(SelectedCar) Else perCar(SelectedCar) = offset
            car.Invalidate()
        End If
        Edited()
    End Sub

    Private Sub Edited()
        UpdateSummary()
        If Not updating Then RaiseEvent Changed()
    End Sub

    Private Sub UpdateSummary()
        If everyCar.Checked Then
            summary.Text = "Every car: " & everyCarOffset.Describe() & "."
            Return
        End If
        If perCar.Count = 0 Then
            summary.Text = "Every car uses DiRT 3's own seat."
            Return
        End If
        Dim names = perCar.Select(Function(pair) EventCatalog.Current.FindCar(pair.Key).Name & " (" & pair.Value.Describe() & ")").OrderBy(Function(text) text, StringComparer.Ordinal).ToArray()
        summary.Text = names.Length & If(names.Length = 1, " car adjusted: ", " cars adjusted: ") & String.Join("; ", names.Take(4)) & If(names.Length > 4, "; and " & (names.Length - 4) & " more", "") & "."
    End Sub
End Class
