Imports System.Drawing
Imports System.Windows.Forms

' Shows every connected wheel, pedal set, shifter and controller as Windows reports it,
' and tests rumble / force feedback. It assigns nothing and changes no game or Windows
' setting. Raw axis values are before any calibration. Ported from GRID2VR.
Public NotInheritable Class DrivingInputMonitor
    Inherits LauncherForm

    Private ReadOnly libraryPath As String
    Private ReadOnly status As New Label With {.Name = "LiveInputStatus", .AutoSize = True, .Dock = DockStyle.Fill, .UseMnemonic = False}
    Private ReadOnly devicePanel As New TableLayoutPanel With {.Name = "LiveInputDevices", .AutoSize = True, .Dock = DockStyle.Top, .ColumnCount = 1}
    Private ReadOnly views As New List(Of DeviceView)
    Private ReadOnly pollTimer As New Timer With {.Interval = 50}
    Private ReadOnly feedbackTimer As New Timer With {.Interval = 1000}
    Private ReadOnly feedbackNote As New Label With {.Name = "LiveInputFeedback", .AutoSize = True, .MaximumSize = New Size(560, 0), .Margin = New Padding(12, 8, 0, 0), .UseMnemonic = False}
    Private input As DrivingInput
    Private playing As DrivingInput.Device

    Public Sub New(libraryPath As String)
        Me.libraryPath = libraryPath
        Text = "DiRT3VR — Live input and force feedback" : ClientSize = New Size(900, 720) : MinimumSize = New Size(660, 440)
        StartPosition = FormStartPosition.CenterParent : AutoScaleMode = AutoScaleMode.Dpi
        Dim layout As New TableLayoutPanel With {.Dock = DockStyle.Fill, .Padding = New Padding(16), .ColumnCount = 1, .RowCount = 4}
        layout.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 100))
        layout.RowStyles.Add(New RowStyle(SizeType.AutoSize)) : layout.RowStyles.Add(New RowStyle(SizeType.AutoSize))
        layout.RowStyles.Add(New RowStyle(SizeType.Percent, 100)) : layout.RowStyles.Add(New RowStyle(SizeType.AutoSize))
        layout.Controls.Add(New Label With {.Text = "Move your wheel, pedals, shifter or controller to see what Windows reports. Axis values are before calibration, so a released pedal may read −100% or +100%. Nothing here changes DiRT 3's controls. Refresh after connecting a device.", .AutoSize = True, .MaximumSize = New Size(820, 0), .Margin = New Padding(0, 0, 0, 10), .UseMnemonic = False}, 0, 0)
        layout.Controls.Add(status, 0, 1)
        Dim scroller As New Panel With {.Dock = DockStyle.Fill, .AutoScroll = True, .Margin = New Padding(0, 10, 0, 10)}
        devicePanel.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 100)) : scroller.Controls.Add(devicePanel) : layout.Controls.Add(scroller, 0, 2)
        Dim buttons As New FlowLayoutPanel With {.AutoSize = True, .Dock = DockStyle.Fill}
        Dim refresh As New Button With {.Name = "LiveInputRefresh", .Text = "Refresh devices", .AutoSize = True}
        AddHandler refresh.Click, Sub() RefreshDevices()
        Dim closeButton As New Button With {.Name = "LiveInputClose", .Text = "Close", .AutoSize = True, .DialogResult = DialogResult.OK}
        CancelButton = closeButton
        buttons.Controls.AddRange({refresh, closeButton, feedbackNote}) : layout.Controls.Add(buttons, 0, 3) : Controls.Add(layout)
        AddHandler pollTimer.Tick, Sub() Poll()
        AddHandler feedbackTimer.Tick, Sub() StopFeedback()
        AddHandler Shown, Sub() RefreshDevices()
    End Sub

    Public ReadOnly Property DeviceCount As Integer
        Get
            Return views.Count
        End Get
    End Property

    Private Sub TestFeedback(device As DrivingInput.Device)
        If input Is Nothing Then Return
        StopFeedback()
        Dim result = input.PlayFeedback(device, 1000)
        Dim failure = 0, failedStep = If(result < 0, input.FeedbackFailure(failure), 0UI)
        feedbackNote.Text = DrivingInput.FeedbackMessage(device, result, failedStep, failure, input.FeedbackShapeResults())
        If result = DrivingInput.FeedbackStarted Then playing = device : feedbackTimer.Start()
    End Sub

    Private Sub StopFeedback()
        feedbackTimer.Stop()
        If playing IsNot Nothing AndAlso input IsNot Nothing Then input.PlayFeedback(playing, 0)
        playing = Nothing
    End Sub

    Public Sub RefreshDevices()
        StopFeedback() : pollTimer.Stop()
        input?.Dispose() : input = Nothing : views.Clear()
        devicePanel.SuspendLayout()
        Try
            Do While devicePanel.Controls.Count > 0
                Dim control = devicePanel.Controls(0)
                devicePanel.Controls.RemoveAt(0) : control.Dispose()
            Loop
            devicePanel.RowStyles.Clear() : devicePanel.RowCount = 0
            input = New DrivingInput(libraryPath, Handle)
            For Each device In input.Devices
                Dim view As New DeviceView(device)
                views.Add(view) : AddRow(view.Panel)
                view.Test.Enabled = input.FeedbackAvailable
                Dim target = view.Device
                AddHandler view.Test.Click, Sub() TestFeedback(target)
            Next
            LauncherAppearance.Apply(devicePanel)
            Poll() : pollTimer.Start()
        Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is DllNotFoundException OrElse TypeOf ex Is BadImageFormatException OrElse TypeOf ex Is EntryPointNotFoundException
            input?.Dispose() : input = Nothing
            status.Text = ex.Message
        Finally
            devicePanel.ResumeLayout(True)
        End Try
    End Sub

    Private Sub AddRow(control As Control)
        devicePanel.RowStyles.Add(New RowStyle(SizeType.AutoSize))
        devicePanel.Controls.Add(control, 0, devicePanel.RowCount)
        devicePanel.RowCount += 1
    End Sub

    Private Sub Poll()
        If input Is Nothing Then Return
        Dim connected = 0
        For Each view In views
            Dim value = input.Read(view.Device)
            view.Update(value)
            If value.Connected <> 0UI Then connected += 1
        Next
        status.Text = If(views.Count = 0, "No controllers detected. Connect a device, then refresh.", "Live — " & connected & " of " & views.Count & " detected devices connected")
    End Sub

    Protected Overrides Sub OnDeactivate(e As EventArgs)
        StopFeedback() : pollTimer.Stop()
        If input IsNot Nothing Then status.Text = "Paused while this window is inactive."
        MyBase.OnDeactivate(e)
    End Sub

    Protected Overrides Sub OnActivated(e As EventArgs)
        MyBase.OnActivated(e)
        If input IsNot Nothing Then Poll() : pollTimer.Start()
    End Sub

    Protected Overrides Sub Dispose(disposing As Boolean)
        If disposing Then
            StopFeedback() : pollTimer.Dispose() : feedbackTimer.Dispose()
            input?.Dispose() : input = Nothing
        End If
        MyBase.Dispose(disposing)
    End Sub

    Private NotInheritable Class DeviceView
        Public ReadOnly Property Device As DrivingInput.Device
        Public ReadOnly Property Panel As GroupBox
        Public ReadOnly Property Test As Button
        Private ReadOnly connection As New Label With {.AutoSize = True, .UseMnemonic = False}
        Private ReadOnly buttonsLabel As New Label With {.AutoSize = True, .MaximumSize = New Size(560, 0)}
        Private ReadOnly hatsLabel As New Label With {.AutoSize = True}
        Private ReadOnly values(7) As Label
        Private ReadOnly bars(7) As AxisMeter

        Public Sub New(device As DrivingInput.Device)
            Me.Device = device
            Panel = New GroupBox With {.Text = device.Name, .AutoSize = True, .AutoSizeMode = AutoSizeMode.GrowAndShrink, .Dock = DockStyle.Top, .Padding = New Padding(10, 22, 10, 10), .Margin = New Padding(0, 0, 0, 12)}
            Dim grid As New TableLayoutPanel With {.AutoSize = True, .Dock = DockStyle.Top, .ColumnCount = 3}
            grid.ColumnStyles.Add(New ColumnStyle(SizeType.Absolute, 170)) : grid.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 100)) : grid.ColumnStyles.Add(New ColumnStyle(SizeType.Absolute, 100))
            connection.Text = device.Id : grid.Controls.Add(connection, 0, 0) : grid.SetColumnSpan(connection, 3)
            Dim names = If(device.Xbox, {"Left stick X", "Left stick Y", "Right stick X", "Right stick Y", "Left trigger", "Right trigger", "—", "—"},
                                        {"X axis", "Y axis", "Z axis", "X rotation", "Y rotation", "Z rotation", "Slider 0", "Slider 1"})
            For i = 0 To 7
                grid.Controls.Add(New Label With {.Text = names(i), .AutoSize = True, .Anchor = AnchorStyles.Left}, 0, i + 1)
                bars(i) = New AxisMeter(device.Xbox AndAlso i >= 4) With {.Dock = DockStyle.Fill, .Height = 18, .Margin = New Padding(3, 6, 3, 6)}
                grid.Controls.Add(bars(i), 1, i + 1)
                values(i) = New Label With {.Text = "—", .AutoSize = True, .Anchor = AnchorStyles.Right}
                grid.Controls.Add(values(i), 2, i + 1)
            Next
            grid.Controls.Add(New Label With {.Text = If(device.Xbox, "Buttons", "Buttons (from 0)"), .AutoSize = True, .Anchor = AnchorStyles.Left}, 0, 9)
            grid.Controls.Add(buttonsLabel, 1, 9) : grid.SetColumnSpan(buttonsLabel, 2)
            grid.Controls.Add(New Label With {.Text = "Hats (from 0)", .AutoSize = True, .Anchor = AnchorStyles.Left}, 0, 10)
            grid.Controls.Add(hatsLabel, 1, 10) : grid.SetColumnSpan(hatsLabel, 2)
            Test = New Button With {.Name = "LiveInputFeedbackTest", .Text = If(device.Xbox, "Test rumble", "Test force feedback"), .AutoSize = True, .Anchor = AnchorStyles.Left, .Margin = New Padding(3, 8, 3, 3)}
            grid.Controls.Add(Test, 0, 11) : grid.SetColumnSpan(Test, 3)
            Panel.Controls.Add(grid)
        End Sub

        Public Sub Update(value As DrivingInput.Sample)
            connection.Text = If(value.Connected <> 0UI, "Connected · ", "Disconnected · ") & Device.Id
            For i = 0 To 7
                Dim available = value.Connected <> 0UI AndAlso (value.Axes And (1UI << i)) <> 0UI AndAlso Single.IsFinite(value.Values(i))
                bars(i).Enabled = available
                If Not available Then
                    bars(i).SetValue(0) : values(i).Text = "—"
                    Continue For
                End If
                Dim reading = Math.Clamp(value.Values(i), -1.0F, 1.0F)
                bars(i).SetValue(reading)
                values(i).Text = reading.ToString("+0.0%;−0.0%;0.0%", Globalization.CultureInfo.InvariantCulture)
            Next
            buttonsLabel.Text = DrivingInput.ButtonsText(Device, value)
            hatsLabel.Text = If(Device.Xbox, "Shown under buttons", DrivingInput.HatsText(value))
        End Sub
    End Class

    ' Native progress bars animate toward their target and lag a wheel's live position,
    ' so paint the current sample directly, with a neutral marker.
    Private NotInheritable Class AxisMeter
        Inherits Control
        Private ReadOnly positive As Boolean
        Private reading As Single

        Public Sub New(positive As Boolean)
            Me.positive = positive
            DoubleBuffered = True
        End Sub

        Public Sub SetValue(sample As Single)
            If reading = sample Then Return
            reading = sample : Invalidate()
        End Sub

        Protected Overrides Sub OnPaint(e As PaintEventArgs)
            MyBase.OnPaint(e)
            Dim bounds As New Rectangle(0, 0, Math.Max(0, Width - 1), Math.Max(0, Height - 1))
            If bounds.Width < 2 OrElse bounds.Height < 2 Then Return
            Dim styled = LauncherAppearance.Modern AndAlso Not SystemInformation.HighContrast
            Using background As New SolidBrush(If(styled, LauncherAppearance.Cell, SystemColors.ControlLight)), edge As New Pen(If(styled, LauncherAppearance.Line, SystemColors.ControlDark))
                e.Graphics.FillRectangle(background, bounds) : e.Graphics.DrawRectangle(edge, bounds)
                Dim origin = If(positive, 1, bounds.Width \ 2)
                If Not positive Then e.Graphics.DrawLine(edge, origin, 1, origin, bounds.Height - 1)
                If Not Enabled Then Return
                Dim position = CInt(Math.Round(If(positive, Math.Max(0F, reading), (reading + 1.0F) / 2.0F) * (bounds.Width - 2))) + 1
                Using fill As New SolidBrush(If(styled, LauncherAppearance.Accent, SystemColors.Highlight))
                    e.Graphics.FillRectangle(fill, Math.Min(origin, position), 2, Math.Max(1, Math.Abs(position - origin)), Math.Max(1, bounds.Height - 3))
                End Using
            End Using
        End Sub
    End Class
End Class
