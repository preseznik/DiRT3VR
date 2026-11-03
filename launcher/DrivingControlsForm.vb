Imports System.Globalization
Imports System.Drawing
Imports System.Runtime.InteropServices
Imports System.Windows.Forms

' The launcher's driving bindings: binding wizard, single-action rebinding, Xbox preset,
' calibration and saved driving presets. Ported from GRID2VR's launcher/DrivingControlsForm.cs.
Public NotInheritable Class DrivingControlsForm
    Inherits LauncherForm

    Public Const RuntimeNotice As String = "Keyboard, controller, wheel, pedals and shifter work together, one binding per action on each device. Actions without a launcher binding keep DiRT 3's own binding."

    Private ReadOnly context As InstallContext
    Private ReadOnly libraryPath As String
    Private model As DrivingControls
    Private ReadOnly enabledBox As New CheckBox With {.Name = "DrivingEnabled", .Text = "Use launcher driving bindings", .AutoSize = True}
    Private ReadOnly rows As New ListView With {.Name = "DrivingRows", .View = View.Details, .FullRowSelect = True, .MultiSelect = False, .Dock = DockStyle.Fill, .HideSelection = False, .ShowItemToolTips = True}
    Private ReadOnly bindKeyboard As Button
    Private ReadOnly bindDevice As Button
    Private ReadOnly calibrateButton As Button
    Private ReadOnly removeButton As Button
    Private ReadOnly resetButton As Button
    ' The device direct practice drives with (#1b46c373): Automatic, then each bound device by DiRT 3's name.
    Private ReadOnly activeDevice As New ComboBox With {.Name = "DrivingActiveDevice", .DropDownStyle = ComboBoxStyle.DropDownList, .Width = 380, .AccessibleName = "Device direct practice drives with"}
    Private ReadOnly deviceNames As New List(Of String)
    ' Direct practice's gearbox (iztok, 2026-11-03: it belongs with the driving controls). It stays a launcher setting
    ' (LauncherSettings.DirectGearbox); Save writes it to the preferences with the bindings.
    Private ReadOnly gearboxBox As New ComboBox With {.Name = "DrivingGearbox", .DropDownStyle = ComboBoxStyle.DropDownList, .Width = 380, .AccessibleName = "Gearbox for direct practice"}
    Private refreshingDevices As Boolean
    ' Live highlight (#9ff647cf): bound rows light up while their input is pressed or moved.
    Private ReadOnly pollTimer As New Timer With {.Interval = 50}
    Private ReadOnly rests As New Dictionary(Of String, DrivingInput.Sample)(StringComparer.OrdinalIgnoreCase)
    Private input As DrivingInput

    <Runtime.InteropServices.DllImport("user32.dll")> Private Shared Function GetAsyncKeyState(key As Integer) As Short
    End Function

    Public Sub New(context As InstallContext, Optional libraryPath As String = Nothing)
        Me.context = context
        Me.libraryPath = If(libraryPath, DrivingInput.LibraryPath(context))
        model = DrivingControls.Load(context)
        Text = "DiRT3VR — Driving controls" : ClientSize = New Size(980, 700) : MinimumSize = New Size(760, 560)
        StartPosition = FormStartPosition.CenterParent : AutoScaleMode = AutoScaleMode.Dpi
        enabledBox.Checked = model.Enabled
        rows.Columns.Add("Action", 170) : rows.Columns.Add("Device", 280) : rows.Columns.Add("Input", 230)
        rows.Columns.Add("Dead zone", 100) : rows.Columns.Add("Saturation", 110)
        ' Drawn here so a lit row shows the accent even while selected: the list selects Steer Left when it opens,
        ' and the system's selection colour hid its highlight (iztok, 2026-11-03).
        rows.OwnerDraw = True
        AddHandler rows.DrawColumnHeader, Sub(sender, e) e.DrawDefault = True
        AddHandler rows.DrawItem, Sub(sender, e) e.DrawDefault = False
        AddHandler rows.DrawSubItem, AddressOf DrawRowCell
        AddHandler rows.MouseMove, Sub(sender, e)
                                       Dim hovered = rows.GetItemAt(e.X, e.Y)
                                       If hovered IsNot Nothing Then rows.Invalidate(hovered.Bounds)
                                   End Sub
        Dim layout As New TableLayoutPanel With {.Dock = DockStyle.Fill, .Padding = New Padding(16), .RowCount = 8, .ColumnCount = 1}
        layout.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 100))
        For i = 0 To 7
            layout.RowStyles.Add(If(i = 3, New RowStyle(SizeType.Percent, 100), New RowStyle(SizeType.AutoSize)))
        Next
        layout.Controls.Add(enabledBox, 0, 0)
        layout.Controls.Add(New Label With {.Name = "DrivingNotice", .Text = RuntimeNotice, .AutoSize = True, .MaximumSize = New Size(900, 0), .Margin = New Padding(0, 8, 0, 12), .UseMnemonic = False}, 0, 1)

        Dim setup = Toolbar("DrivingSetup", "Setup")
        AddButton(setup.Item2, "DrivingWizard", "Binding wizard…", Sub() CaptureBinding(Nothing, False))
        AddButton(setup.Item2, "DrivingXboxPreset", "Xbox preset", Sub()
                                                                         model.ApplyXboxPreset() : enabledBox.Checked = True : RefreshRows()
                                                                     End Sub)
        AddButton(setup.Item2, "DrivingLiveInput", "Live input…", Sub()
                                                                      Using monitor As New DrivingInputMonitor(Me.libraryPath)
                                                                          monitor.ShowDialog(Me)
                                                                      End Using
                                                                  End Sub)
        AddButton(setup.Item2, "DrivingWheelRotation", "Wheel rotation…", Sub() WheelRotation())
        setup.Item1.Margin = New Padding(0, 0, 0, 8) : layout.Controls.Add(setup.Item1, 0, 2)
        layout.Controls.Add(rows, 0, 3)

        Dim selected = Toolbar("DrivingSelection", "Selected")
        bindKeyboard = AddButton(selected.Item2, "DrivingBindKeyboard", "Key…", Sub() CaptureBinding(SelectedAction(), True))
        bindKeyboard.AccessibleName = "Bind a keyboard key to the selected action"
        bindDevice = AddButton(selected.Item2, "DrivingBindDevice", "Device…", Sub() CaptureBinding(SelectedAction(), False))
        bindDevice.AccessibleName = "Bind a controller, wheel or pedal input to the selected action"
        calibrateButton = AddButton(selected.Item2, "DrivingCalibrate", "Calibrate…", Sub() Calibrate())
        removeButton = AddButton(selected.Item2, "DrivingRemoveBinding", "Remove", Sub()
                                                                                    model.Bindings.Remove(SelectedBinding()) : RefreshRows()
                                                                                End Sub)
        resetButton = AddButton(selected.Item2, "DrivingResetAction", "Reset action", Sub()
                                                                                       Dim action = SelectedAction()
                                                                                       model.Bindings.RemoveAll(Function(b) b.Action = action) : RefreshRows()
                                                                                   End Sub)
        selected.Item1.Margin = New Padding(0, 8, 0, 0) : layout.Controls.Add(selected.Item1, 0, 4)

        Dim drives = Toolbar("DrivingActiveDeviceRow", "Drives with")
        drives.Item2.Controls.Add(activeDevice)
        drives.Item2.Controls.Add(New Label With {.Name = "DrivingActiveDeviceNote", .Text = "Direct practice drives with one device; the others wait.", .AutoSize = True, .Anchor = AnchorStyles.Left, .Margin = New Padding(8, 6, 0, 0), .UseMnemonic = False})
        drives.Item1.Margin = New Padding(0, 8, 0, 0) : layout.Controls.Add(drives.Item1, 0, 5)
        AddHandler activeDevice.SelectedIndexChanged, Sub()
                                                          If refreshingDevices Then Return
                                                          model.ActiveDevice = If(activeDevice.SelectedIndex <= 0, "", deviceNames(activeDevice.SelectedIndex - 1))
                                                      End Sub
        Dim gears = Toolbar("DrivingGearboxRow", "Gearbox")
        gearboxBox.Items.AddRange(DirectPractice.GearboxNames)
        Me.Gearbox = SavedGearbox()
        gears.Item2.Controls.Add(gearboxBox)
        gears.Item2.Controls.Add(New Label With {.Name = "DrivingGearboxNote", .Text = "For direct practice; DiRT 3's menus keep your profile's gearbox.", .AutoSize = True, .Anchor = AnchorStyles.Left, .Margin = New Padding(8, 6, 0, 0), .UseMnemonic = False})
        gears.Item1.Margin = New Padding(0, 8, 0, 0) : layout.Controls.Add(gears.Item1, 0, 6)

        Dim presets = Toolbar("DrivingPresets", "Presets")
        AddButton(presets.Item2, "DrivingSavePreset", "Save…", Sub() SavePreset())
        AddButton(presets.Item2, "DrivingLoadPreset", "Load…", Sub() LoadPreset())
        Dim dialogButtons As New FlowLayoutPanel With {.Name = "DrivingDialogButtons", .AutoSize = True, .WrapContents = False, .Dock = DockStyle.Fill, .Margin = Padding.Empty}
        AddButton(dialogButtons, "DrivingSave", "Save", Sub() SaveAndClose())
        Dim cancel As New Button With {.Name = "DrivingCancel", .Text = "Cancel", .AutoSize = True, .DialogResult = DialogResult.Cancel}
        dialogButtons.Controls.Add(cancel) : CancelButton = cancel
        Dim footer As New TableLayoutPanel With {.Name = "DrivingFooter", .Dock = DockStyle.Fill, .AutoSize = True, .ColumnCount = 2, .RowCount = 1, .Margin = New Padding(0, 12, 0, 0)}
        footer.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 100)) : footer.ColumnStyles.Add(New ColumnStyle(SizeType.AutoSize))
        footer.Controls.Add(presets.Item1) : footer.Controls.Add(dialogButtons) : layout.Controls.Add(footer, 0, 7)
        AddHandler rows.SelectedIndexChanged, Sub() UpdateSelection()
        Controls.Add(layout) : RefreshRows()
        AddHandler Shown, Sub() StartHighlight()
        AddHandler pollTimer.Tick, Sub() PollHighlight()
    End Sub

    Private Sub StartHighlight()
        Try
            input = New DrivingInput(libraryPath, Handle)
            pollTimer.Start()
        Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is DllNotFoundException OrElse TypeOf ex Is BadImageFormatException OrElse TypeOf ex Is EntryPointNotFoundException
            ' No live highlight without the input library; binding still works through the wizard's own message.
        End Try
    End Sub

    Private Sub PollHighlight()
        If input Is Nothing Then Return
        Dim samples As New Dictionary(Of String, DrivingInput.Sample)(StringComparer.OrdinalIgnoreCase)
        For Each device In input.Devices
            samples(device.Id) = input.Read(device)
        Next
        Dim focused = ContainsFocus
        ShowActive(samples, Function(key) focused AndAlso GetAsyncKeyState(key) < 0)
    End Sub

    ' Colours each bound row whose input is pressed or moved now with the launcher's accent; also used by tests.
    ' The first connected sample of each device is its rest.
    Public Sub ShowActive(samples As IReadOnlyDictionary(Of String, DrivingInput.Sample), keyDown As Func(Of Integer, Boolean))
        For Each pair In samples
            If pair.Value.Connected <> 0UI AndAlso Not rests.ContainsKey(pair.Key) Then rests(pair.Key) = DrivingCapture.Clone(pair.Value)
        Next
        Dim accent = LauncherAppearance.Accent
        Dim accentText = If(accent.GetBrightness() > 0.5F, Color.Black, Color.White)
        rows.BeginUpdate()
        For Each row As ListViewItem In rows.Items
            Dim binding = TryCast(row.Tag, DrivingBinding)
            Dim sample = DrivingInput.Sample.Empty(), rest = DrivingInput.Sample.Empty()
            Dim active As Boolean
            If binding Is Nothing Then
                active = False
            ElseIf binding.Keyboard Then
                active = keyDown IsNot Nothing AndAlso keyDown(Convert.ToInt32(binding.Input.Substring(10), 16))
            Else
                active = samples.TryGetValue(binding.DeviceId, sample) AndAlso rests.TryGetValue(binding.DeviceId, rest) AndAlso DrivingCapture.Active(binding, sample, rest)
            End If
            Dim back = If(active, accent, rows.BackColor), fore = If(active, accentText, rows.ForeColor)
            If row.BackColor <> back Then row.BackColor = back
            If row.ForeColor <> fore Then row.ForeColor = fore
        Next
        rows.EndUpdate()
    End Sub

    ' The bound devices, Automatic first; a pick whose device has no bindings left goes back to Automatic.
    Private Sub RefreshDevices()
        refreshingDevices = True
        Try
            Dim devices = model.DrivingDevices()
            activeDevice.Items.Clear() : deviceNames.Clear()
            activeDevice.Items.Add("Automatic: the device bound to steering")
            For Each device In devices
                deviceNames.Add(device.Name) : activeDevice.Items.Add(device.Label)
            Next
            Dim index = deviceNames.IndexOf(If(model.ActiveDevice, ""))
            If index < 0 Then model.ActiveDevice = ""
            activeDevice.SelectedIndex = index + 1
            activeDevice.Enabled = devices.Count > 1
        Finally
            refreshingDevices = False
        End Try
    End Sub

    ' A lit row keeps the accent when selected; otherwise the selection colour, else the row's own colours.
    Private Sub DrawRowCell(sender As Object, e As DrawListViewSubItemEventArgs)
        Dim lit = e.Item.BackColor = LauncherAppearance.Accent AndAlso e.Item.Tag IsNot Nothing
        Dim back = If(lit, e.Item.BackColor, If(e.Item.Selected, SystemColors.Highlight, rows.BackColor))
        Dim fore = If(lit, e.Item.ForeColor, If(e.Item.Selected, SystemColors.HighlightText, rows.ForeColor))
        Using brush As New SolidBrush(back)
            e.Graphics.FillRectangle(brush, e.Bounds)
        End Using
        Dim text = If(e.SubItem IsNot Nothing, e.SubItem.Text, e.Item.Text)
        Dim area = Rectangle.FromLTRB(e.Bounds.Left + 6, e.Bounds.Top, e.Bounds.Right - 2, e.Bounds.Bottom)
        TextRenderer.DrawText(e.Graphics, text, rows.Font, area, fore, TextFormatFlags.Left Or TextFormatFlags.VerticalCenter Or TextFormatFlags.EndEllipsis Or TextFormatFlags.SingleLine Or TextFormatFlags.NoPrefix)
        If e.ColumnIndex = 0 AndAlso e.Item.Focused AndAlso rows.Focused AndAlso e.Item.Selected Then ControlPaint.DrawFocusRectangle(e.Graphics, e.Item.Bounds, fore, back)
    End Sub

    ' The colours a row is drawn with now, for tests: (back, fore).
    Public Function RowColors(action As String) As (Back As Color, Fore As Color)
        Dim item = rows.Items.Cast(Of ListViewItem)().First(Function(row) row.Text = action)
        Dim lit = item.BackColor = LauncherAppearance.Accent AndAlso item.Tag IsNot Nothing
        Return If(lit, (item.BackColor, item.ForeColor), If(item.Selected, (SystemColors.Highlight, SystemColors.HighlightText), (rows.BackColor, rows.ForeColor)))
    End Function

    ' Rows lit by the live highlight, for tests.
    Public ReadOnly Property HighlightedActions As IReadOnlyList(Of String)
        Get
            Return rows.Items.Cast(Of ListViewItem)().Where(Function(r) r.BackColor = LauncherAppearance.Accent AndAlso r.Tag IsNot Nothing).Select(Function(r) r.Text).ToArray()
        End Get
    End Property

    Protected Overrides Sub Dispose(disposing As Boolean)
        If disposing Then pollTimer.Dispose() : input?.Dispose() : input = Nothing
        MyBase.Dispose(disposing)
    End Sub

    ' The bindings as edited, for tests and the caller.
    Public ReadOnly Property Current As DrivingControls
        Get
            ReadOptions()
            Return model
        End Get
    End Property

    Private Function AddButton(panel As FlowLayoutPanel, name As String, label As String, run As Action) As Button
        Dim button As New Button With {.Name = name, .Text = label, .AutoSize = True}
        AddHandler button.Click, Sub() Guard(run)
        panel.Controls.Add(button)
        Return button
    End Function

    Private Shared Function Toolbar(name As String, caption As String) As Tuple(Of TableLayoutPanel, FlowLayoutPanel)
        Dim row As New TableLayoutPanel With {.Name = name, .Dock = DockStyle.Fill, .AutoSize = True, .ColumnCount = 2, .RowCount = 1, .Margin = Padding.Empty}
        row.ColumnStyles.Add(New ColumnStyle(SizeType.AutoSize)) : row.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 100))
        Dim buttons As New FlowLayoutPanel With {.Name = name & "Actions", .Dock = DockStyle.Fill, .AutoSize = True, .WrapContents = False, .Margin = Padding.Empty}
        row.Controls.Add(New Label With {.Text = caption, .AutoSize = True, .MinimumSize = New Size(80, 0), .Anchor = AnchorStyles.Left, .Margin = New Padding(0, 0, 8, 0)})
        row.Controls.Add(buttons)
        Return Tuple.Create(row, buttons)
    End Function

    Private Sub Guard(run As Action)
        Try
            run()
        Catch ex As IOException
            MessageBox.Show(Me, ex.Message, "Driving controls", MessageBoxButtons.OK, MessageBoxIcon.Warning)
        Catch ex As UnauthorizedAccessException
            MessageBox.Show(Me, ex.Message, "Driving controls", MessageBoxButtons.OK, MessageBoxIcon.Warning)
        End Try
    End Sub

    Private Function SelectedAction() As String
        If rows.SelectedItems.Count <> 1 Then Throw New IOException("Select an action first.")
        Return rows.SelectedItems(0).Text
    End Function

    Private Function SelectedBinding() As DrivingBinding
        Dim binding = If(rows.SelectedItems.Count = 1, TryCast(rows.SelectedItems(0).Tag, DrivingBinding), Nothing)
        If binding Is Nothing Then Throw New IOException("Select a launcher binding first.")
        Return binding
    End Function

    Private Sub RefreshRows()
        Dim selectedAction = If(rows.SelectedItems.Count = 1, rows.SelectedItems(0).Text, DrivingControls.Actions(0))
        Dim selectedDevice = If(rows.SelectedItems.Count = 1, TryCast(rows.SelectedItems(0).Tag, DrivingBinding)?.DeviceId, Nothing)
        rows.BeginUpdate() : rows.Items.Clear()
        For Each action In DrivingControls.Actions
            Dim bound = model.Bindings.Where(Function(b) b.Action = action).OrderBy(Function(b) Not b.Keyboard).ToArray()
            For Each binding In If(bound.Length = 0, New DrivingBinding() {Nothing}, bound)
                Dim row As New ListViewItem(action) With {.Tag = binding}
                row.SubItems.Add(If(binding Is Nothing, "DiRT 3's binding", binding.Device))
                row.SubItems.Add(If(binding Is Nothing, "—", DrivingCapture.InputDescription(binding)))
                row.SubItems.Add(If(binding Is Nothing OrElse binding.Keyboard, "—", binding.DeadZone.ToString("P0", Globalization.CultureInfo.CurrentCulture)))
                row.SubItems.Add(If(binding Is Nothing OrElse binding.Keyboard, "—", binding.Saturation.ToString("P0", Globalization.CultureInfo.CurrentCulture)))
                row.ToolTipText = If(binding Is Nothing, "No launcher binding: DiRT 3's own binding is used.", DrivingCapture.Description(binding) & Environment.NewLine & binding.DeviceId)
                rows.Items.Add(row)
                row.Selected = action = selectedAction AndAlso String.Equals(binding?.DeviceId, selectedDevice, StringComparison.OrdinalIgnoreCase)
            Next
        Next
        rows.EndUpdate()
        If rows.SelectedItems.Count = 0 Then
            Dim fallback = rows.Items.Cast(Of ListViewItem)().FirstOrDefault(Function(r) r.Text = selectedAction)
            If fallback IsNot Nothing Then fallback.Selected = True
        End If
        RefreshDevices()
        UpdateSelection()
    End Sub

    Private Sub UpdateSelection()
        Dim selected = If(rows.SelectedItems.Count = 1, rows.SelectedItems(0), Nothing)
        Dim binding = TryCast(selected?.Tag, DrivingBinding)
        bindKeyboard.Enabled = selected IsNot Nothing : bindDevice.Enabled = selected IsNot Nothing
        calibrateButton.Enabled = binding IsNot Nothing AndAlso Not binding.Keyboard
        removeButton.Enabled = binding IsNot Nothing
        resetButton.Enabled = selected IsNot Nothing AndAlso model.Bindings.Any(Function(b) b.Action = selected.Text)
    End Sub

    Private Sub CaptureBinding(action As String, keyboard As Boolean)
        Using dialog As New DrivingBindingForm(libraryPath, action, keyboard, model.ListenAllDevices)
            If dialog.ShowDialog(Me) <> DialogResult.OK Then Return
            model.ListenAllDevices = dialog.ListenAllDevices
            ApplyBindings(dialog.Bindings)
        End Using
    End Sub

    ' Adds captured bindings (the wizard's or one action's); also used by tests.
    Public Sub ApplyBindings(captured As IEnumerable(Of DrivingBinding))
        Dim any = False
        For Each binding In captured
            model.Assign(binding) : any = True
        Next
        If any Then enabledBox.Checked = True
        RefreshRows()
    End Sub

    ' Wheel rotation (#a72b44ea, as DiRT2VR 0.18): the steering saturation from the wheel's rotation and
    ' the rotation wanted in DiRT 3.
    Private Sub WheelRotation()
        If model.SteeringWheel() Is Nothing Then Throw New IOException("Bind Steer Left and Steer Right to your wheel first, with the Binding wizard or Device….")
        Using dialog As New WheelRotationForm(model)
            If dialog.ShowDialog(Me) = DialogResult.OK Then RefreshRows()
        End Using
    End Sub

    Private Sub Calibrate()
        Dim binding = SelectedBinding()
        If binding.Keyboard Then Throw New IOException("Select a controller, wheel or pedal binding to calibrate.")
        Using dialog As New DrivingCalibrationForm(binding)
            If dialog.ShowDialog(Me) = DialogResult.OK Then model.Assign(dialog.Binding) : RefreshRows()
        End Using
    End Sub

    Private Sub SavePreset()
        Dim folder = DrivingControls.PresetsPath(context)
        SafeFiles.NoLinks(folder) : Directory.CreateDirectory(folder)
        Using dialog As New SaveFileDialog With {.Title = "Save driving preset", .Filter = "Driving presets (*.json)|*.json", .DefaultExt = "json",
                                                 .AddExtension = True, .InitialDirectory = folder, .FileName = "Driving controls.json", .OverwritePrompt = True}
            If dialog.ShowDialog(Me) <> DialogResult.OK Then Return
            ReadOptions() : model.SaveFile(dialog.FileName)
        End Using
    End Sub

    Private Sub LoadPreset()
        Using dialog As New OpenFileDialog With {.Title = "Load driving preset", .Filter = "Driving presets (*.json)|*.json", .InitialDirectory = DrivingControls.PresetsPath(context)}
            If dialog.ShowDialog(Me) <> DialogResult.OK Then Return
            UsePreset(DrivingControls.LoadFile(dialog.FileName))
        End Using
    End Sub

    ' Replaces the edited bindings with a loaded preset; nothing is saved until Save.
    Public Sub UsePreset(loaded As DrivingControls)
        model = loaded : enabledBox.Checked = loaded.Enabled : RefreshRows()
    End Sub

    Private Sub ReadOptions()
        model.Enabled = enabledBox.Checked
    End Sub

    ' The gearbox shown here: a DirectPractice.GearboxChoices value.
    <System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)>
    Public Property Gearbox As String
        Get
            Return DirectPractice.GearboxChoices(Math.Max(0, gearboxBox.SelectedIndex))
        End Get
        Set(value As String)
            gearboxBox.SelectedIndex = Math.Max(0, Array.IndexOf(DirectPractice.GearboxChoices, value))
        End Set
    End Property

    Private Function SavedGearbox() As String
        Try
            Return LauncherSettings.Load(context).DirectGearbox
        Catch ex As IOException
            Return DirectPractice.GearboxChoices(0)
        End Try
    End Function

    Private Sub SaveAndClose()
        ReadOptions() : model.Save(context)
        If Gearbox <> SavedGearbox() Then
            Dim preferences = LauncherSettings.Load(context)
            preferences.DirectGearbox = Gearbox
            preferences.Save(context)
        End If
        DialogResult = DialogResult.OK : Close()
    End Sub
End Class

Public NotInheritable Class DrivingCalibrationForm
    Inherits LauncherForm

    Private ReadOnly original As DrivingBinding
    Private ReadOnly direction As New ComboBox With {.Name = "CalibrationDirection", .AccessibleName = "Axis direction", .DropDownStyle = ComboBoxStyle.DropDownList, .Width = 240}
    Private ReadOnly dead As New NumericUpDown With {.Name = "DeadZonePercent", .AccessibleName = "Dead zone percent", .Minimum = 0, .Maximum = 90, .DecimalPlaces = 2, .Width = 92, .Anchor = AnchorStyles.Right}
    Private ReadOnly saturationBox As New NumericUpDown With {.Name = "SaturationPercent", .AccessibleName = "Saturation percent", .Minimum = 10, .Maximum = 100, .DecimalPlaces = 2, .Width = 92, .Anchor = AnchorStyles.Right}

    Public Sub New(binding As DrivingBinding)
        original = binding
        Text = "Calibration — " & binding.Action : ClientSize = New Size(650, 420) : MinimumSize = Size
        StartPosition = FormStartPosition.CenterParent : AutoScaleMode = AutoScaleMode.Dpi
        direction.Items.AddRange({"Pedal or lever (increases)", "Pedal or lever (decreases)", "Centered axis, lower half", "Centered axis, upper half"})
        direction.SelectedIndex = Math.Max(0, Array.IndexOf(DrivingControls.Calibrations, binding.Calibration))
        dead.Value = binding.DeadZone * 100D : saturationBox.Value = binding.Saturation * 100D
        Dim layout As New TableLayoutPanel With {.Dock = DockStyle.Fill, .Padding = New Padding(20), .ColumnCount = 2, .RowCount = 5}
        layout.ColumnStyles.Add(New ColumnStyle(SizeType.AutoSize)) : layout.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 100))
        For i = 0 To 4
            layout.RowStyles.Add(New RowStyle(SizeType.AutoSize))
        Next
        layout.Controls.Add(New Label With {.Text = "Axis direction", .AutoSize = True, .Anchor = AnchorStyles.Left}, 0, 0) : layout.Controls.Add(direction, 1, 0)
        layout.Controls.Add(New Label With {.Text = "Dead zone (%)", .AutoSize = True, .Anchor = AnchorStyles.Left}, 0, 1) : layout.Controls.Add(Slider(dead, "DeadZone"), 1, 1)
        layout.Controls.Add(New Label With {.Text = "Saturation (%)", .AutoSize = True, .Anchor = AnchorStyles.Left}, 0, 2) : layout.Controls.Add(Slider(saturationBox, "Saturation"), 1, 2)
        Dim initial = DrivingControls.DefaultCalibration(binding.Action)
        Dim defaults = If(binding.Action = "Clutch", "The clutch starts at 10% and 80%, as in DiRT 3's own wheel presets, so it counts as fully in before the pedal's end; other axes start at 0% and 100%.", "Defaults are 0% and 100%.")
        Dim help As New Label With {.Text = "Dead zone ignores input near rest. Saturation is the travel needed for full output: 100% uses all of it; lower makes the control more sensitive. " & defaults & " The dead zone must stay below saturation." & Environment.NewLine & Environment.NewLine &
            "A centered axis (steering, sticks) uses half its travel per action; a pedal or lever uses its full travel.", .AutoSize = True, .MaximumSize = New Size(600, 0), .Margin = New Padding(3, 10, 3, 12)}
        layout.Controls.Add(help, 0, 3) : layout.SetColumnSpan(help, 2)
        Dim ok As New Button With {.Name = "CalibrationApply", .Text = "Apply", .AutoSize = True}
        AddHandler ok.Click, Sub()
                                 If dead.Value >= saturationBox.Value Then MessageBox.Show(Me, "The dead zone must be below saturation.") : Return
                                 Dim check As New DrivingControls With {.Bindings = New List(Of DrivingBinding) From {Me.Binding}}
                                 Try
                                     check.Validate()
                                 Catch ex As IOException
                                     MessageBox.Show(Me, ex.Message) : Return
                                 End Try
                                 Dim found = check.Problems()
                                 If found.Count > 0 Then MessageBox.Show(Me, String.Join(Environment.NewLine, found)) : Return
                                 DialogResult = DialogResult.OK : Close()
                             End Sub
        Dim reset As New Button With {.Name = "CalibrationReset", .Text = "Reset to " & Percent(initial.DeadZone) & " / " & Percent(initial.Saturation), .AutoSize = True}
        AddHandler reset.Click, Sub()
                                    dead.Value = initial.DeadZone * 100D : saturationBox.Value = initial.Saturation * 100D
                                End Sub
        Dim cancel As New Button With {.Text = "Cancel", .AutoSize = True, .DialogResult = DialogResult.Cancel}
        CancelButton = cancel
        Dim buttons As New FlowLayoutPanel With {.Dock = DockStyle.Fill, .AutoSize = True, .WrapContents = True}
        buttons.Controls.AddRange({ok, reset, cancel}) : layout.Controls.Add(buttons, 0, 4) : layout.SetColumnSpan(buttons, 2) : Controls.Add(layout)
    End Sub

    Public ReadOnly Property Binding As DrivingBinding
        Get
            Dim result = original.Copy()
            result.Calibration = DrivingControls.Calibrations(direction.SelectedIndex)
            result.DeadZone = dead.Value / 100D : result.Saturation = saturationBox.Value / 100D
            Return result
        End Get
    End Property

    Private Shared Function Percent(value As Decimal) As String
        Return (value * 100D).ToString("0.##", Globalization.CultureInfo.CurrentCulture) & "%"
    End Function

    Private Shared Function Slider(number As NumericUpDown, name As String) As Control
        Dim track As New TrackBar With {.Name = name & "Slider", .AccessibleName = number.AccessibleName, .Minimum = CInt(number.Minimum), .Maximum = CInt(number.Maximum),
                                        .Value = CInt(Math.Round(number.Value)), .SmallChange = 1, .LargeChange = 10, .TickStyle = TickStyle.None, .AutoSize = False, .Dock = DockStyle.Fill, .Margin = Padding.Empty}
        Dim panel As New TableLayoutPanel With {.Dock = DockStyle.Fill, .Height = 38, .MinimumSize = New Size(240, 38), .ColumnCount = 2, .RowCount = 1, .Margin = Padding.Empty}
        panel.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 100)) : panel.ColumnStyles.Add(New ColumnStyle(SizeType.AutoSize))
        panel.RowStyles.Add(New RowStyle(SizeType.Percent, 100))
        panel.Controls.Add(track) : panel.Controls.Add(number)
        Dim synchronizing = False
        AddHandler track.ValueChanged, Sub()
                                           If Not synchronizing Then number.Value = track.Value
                                       End Sub
        AddHandler number.ValueChanged, Sub()
                                            synchronizing = True
                                            Try
                                                track.Value = CInt(Math.Round(number.Value))
                                            Finally
                                                synchronizing = False
                                            End Try
                                        End Sub
        Return panel
    End Function
End Class

' The binding wizard (all batches) or one action's rebinding. Reads input non-exclusively
' through payload\driving_input.dll; DiRT 3 is not involved.
Public NotInheritable Class DrivingBindingForm
    Inherits LauncherForm

    Private ReadOnly libraryPath As String
    Private ReadOnly wizard As DrivingWizard
    Private ReadOnly batchLabel As New Label With {.Name = "WizardBatch", .AutoSize = True, .MaximumSize = New Size(620, 0)}
    Private ReadOnly heading As New Label With {.Name = "WizardHeading", .AutoSize = True, .UseMnemonic = False}
    Private ReadOnly instruction As New Label With {.Name = "WizardInstruction", .AutoSize = True, .MaximumSize = New Size(620, 0)}
    Private ReadOnly status As New Label With {.Name = "WizardStatus", .AutoSize = True, .MaximumSize = New Size(620, 0), .UseMnemonic = False}
    Private ReadOnly source As New ComboBox With {.Name = "WizardSource", .AccessibleName = "Input source", .DropDownStyle = ComboBoxStyle.DropDownList, .Width = 250}
    Private ReadOnly devices As New ComboBox With {.Name = "WizardDevices", .AccessibleName = "Device", .DropDownStyle = ComboBoxStyle.DropDownList, .Width = 500}
    Private ReadOnly allDevices As New CheckBox With {.Name = "WizardAllDevices", .Text = "Detect inputs from all connected devices", .Checked = True, .AutoSize = True}
    Private ReadOnly filter As New ComboBox With {.Name = "WizardFilter", .AccessibleName = "Input kind", .DropDownStyle = ComboBoxStyle.DropDownList, .Width = 200}
    Private ReadOnly refreshButton As New Button With {.Name = "WizardRefresh", .Text = "Refresh devices", .AutoSize = True}
    Private ReadOnly captureButtons As New FlowLayoutPanel With {.Name = "WizardCaptureButtons", .AutoSize = True}
    Private ReadOnly batchButtons As New FlowLayoutPanel With {.Name = "WizardBatchButtons", .AutoSize = True, .Visible = False}
    Private ReadOnly bindBatch As New Button With {.Name = "WizardBindBatch", .AutoSize = True}
    Private ReadOnly pollTimer As New Timer With {.Interval = 40}
    Private ReadOnly captures As New Dictionary(Of String, DrivingCapture)
    Private input As DrivingInput
    Private active As DrivingCapture
    Private held As Keys
    Private releaseAt As Long = -1
    Private pendingKey As DrivingBinding
    Private updating As Boolean
    Private inputFailure As String

    <DllImport("user32.dll")> Private Shared Function GetAsyncKeyState(key As Integer) As Short
    End Function

    Public Sub New(libraryPath As String, action As String, keyboard As Boolean, Optional listenAllDevices As Boolean = True)
        Me.libraryPath = libraryPath
        wizard = New DrivingWizard(If(action Is Nothing, DrivingControls.WizardBatches, New DrivingWizardBatch() {New DrivingWizardBatch(action, action, {action})}))
        batchLabel.Visible = wizard.Batches.Count > 1
        allDevices.Checked = listenAllDevices
        heading.Font = New Font(SystemFonts.MessageBoxFont.FontFamily, 22, FontStyle.Bold)
        Text = If(action Is Nothing, "DiRT3VR — Binding wizard", "DiRT3VR — Bind " & action) : ClientSize = New Size(680, 480) : MinimumSize = New Size(600, 480)
        StartPosition = FormStartPosition.CenterParent : AutoScaleMode = AutoScaleMode.Dpi
        source.Items.AddRange({"Controller / wheel / pedals", "Keyboard"}) : source.SelectedIndex = If(keyboard, 1, 0)
        filter.Items.AddRange({"Automatic input", "Axis only", "Button / hat only"}) : filter.SelectedIndex = 0
        Dim panel As New FlowLayoutPanel With {.Dock = DockStyle.Fill, .FlowDirection = FlowDirection.TopDown, .WrapContents = False, .AutoScroll = True, .Padding = New Padding(20)}
        AddHandler refreshButton.Click, Sub() RefreshDevices()
        Dim skip As New Button With {.Name = "WizardSkip", .Text = "Skip / keep current", .AutoSize = True}
        AddHandler skip.Click, Sub() NextStep(Nothing)
        Dim retry As New Button With {.Name = "WizardRetry", .Text = "Try again", .AutoSize = True}
        AddHandler retry.Click, Sub() BeginStep()
        ' Escape is a valid DiRT 3 binding (Pause), so no Form.CancelButton.
        Dim cancel As New Button With {.Name = "WizardCancel", .Text = "Cancel", .AutoSize = True}
        AddHandler cancel.Click, Sub() Finish()
        captureButtons.Controls.AddRange({skip, retry, cancel})
        AddHandler bindBatch.Click, Sub()
                                        wizard.Accept() : BeginStep()
                                    End Sub
        Dim skipBatch As New Button With {.Name = "WizardSkipBatch", .Text = "Skip batch", .AutoSize = True}
        AddHandler skipBatch.Click, Sub()
                                        wizard.Skip()
                                        If wizard.Done Then Finish() Else BeginStep()
                                    End Sub
        Dim finishButton As New Button With {.Name = "WizardFinish", .Text = "Finish", .AutoSize = True}
        AddHandler finishButton.Click, Sub() Finish()
        batchButtons.Controls.AddRange({bindBatch, skipBatch, finishButton})
        panel.Controls.AddRange({batchLabel, heading, source, allDevices, devices, filter, refreshButton, instruction, status, captureButtons, batchButtons})
        Controls.Add(panel)
        AddHandler source.SelectedIndexChanged, Sub() BeginStep()
        AddHandler devices.SelectedIndexChanged, Sub()
                                                     If updating Then Return
                                                     If devices.SelectedIndex = 0 AndAlso Not allDevices.Checked Then allDevices.Checked = True Else BeginStep()
                                                 End Sub
        AddHandler allDevices.CheckedChanged, Sub()
                                                  updating = True
                                                  If allDevices.Checked AndAlso devices.Items.Count > 0 Then
                                                      devices.SelectedIndex = 0
                                                  ElseIf devices.SelectedIndex <= 0 AndAlso devices.Items.Count > 1 Then
                                                      devices.SelectedIndex = 1
                                                  End If
                                                  updating = False : BeginStep()
                                              End Sub
        AddHandler filter.SelectedIndexChanged, Sub() BeginStep()
        AddHandler pollTimer.Tick, Sub() Poll()
        AddHandler Shown, Sub() RefreshDevices()
        BeginStep()
    End Sub

    Public ReadOnly Property Bindings As IReadOnlyList(Of DrivingBinding)
        Get
            Return wizard.Bindings
        End Get
    End Property

    Public ReadOnly Property ListenAllDevices As Boolean
        Get
            Return allDevices.Checked
        End Get
    End Property

    Public ReadOnly Property Progress As DrivingWizard
        Get
            Return wizard
        End Get
    End Property

    Private Sub RefreshDevices()
        pollTimer.Stop() : input?.Dispose() : input = Nothing : updating = True
        devices.Items.Clear() : devices.Items.Add("All connected devices")
        Dim failure As String = Nothing
        inputFailure = Nothing
        Try
            input = New DrivingInput(libraryPath, Handle)
            For Each device In input.Devices
                devices.Items.Add(device)
            Next
        Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is DllNotFoundException OrElse TypeOf ex Is BadImageFormatException OrElse TypeOf ex Is EntryPointNotFoundException
            failure = ex.Message
        Finally
            devices.SelectedIndex = If(Not allDevices.Checked AndAlso devices.Items.Count > 1, 1, 0)
            updating = False : BeginStep() : pollTimer.Start()
        End Try
        If failure IsNot Nothing Then inputFailure = failure : status.Text = failure
    End Sub

    Private Sub BeginStep()
        captures.Clear() : active = Nothing : held = Keys.None : pendingKey = Nothing : releaseAt = -1
        If wizard.Done Then Return
        If wizard.Batches.Count > 1 Then
            For Each item As Control In New Control() {source, allDevices, devices, filter, refreshButton, status, captureButtons}
                item.Visible = Not wizard.Asking
            Next
            batchButtons.Visible = wizard.Asking
            batchLabel.Text = If(wizard.Asking, "Next: ", "") & wizard.Batch.Name & " — batch " & (wizard.BatchIndex + 1).ToString(Globalization.CultureInfo.InvariantCulture) & " of " & wizard.Batches.Count.ToString(Globalization.CultureInfo.InvariantCulture)
        End If
        If wizard.Asking Then
            heading.Text = wizard.Batch.Name.ToUpperInvariant() & "?"
            instruction.Text = "Bind " & wizard.Batch.Summary & " next (" & wizard.Batch.Actions.Length.ToString(Globalization.CultureInfo.InvariantCulture) & " actions)? Skip batch keeps their current bindings. Finished batches are kept, even if you cancel a later one."
            bindBatch.Text = "Bind " & wizard.Batch.Name : bindBatch.Focus()
            Return
        End If
        Dim action = wizard.Action, count = wizard.Batch.Actions.Length
        heading.Text = action.ToUpperInvariant() & If(count > 1, "  (" & (wizard.Step + 1).ToString(Globalization.CultureInfo.InvariantCulture) & "/" & count.ToString(Globalization.CultureInfo.InvariantCulture) & ")", "")
        filter.Enabled = source.SelectedIndex = 0 : allDevices.Enabled = source.SelectedIndex = 0
        devices.Enabled = source.SelectedIndex = 0 AndAlso Not allDevices.Checked
        If source.SelectedIndex = 1 Then
            instruction.Text = "Press and release a key. Escape can be bound; use Cancel to leave."
        ElseIf action.StartsWith("Gear ", StringComparison.Ordinal) AndAlso action <> "Gear Up" AndAlso action <> "Gear Down" Then
            instruction.Text = "Leave the shifter in neutral, select this gear, then return to neutral. Skip gears your shifter doesn't have. A separate USB shifter works with all-device detection."
        Else
            instruction.Text = "Center the steering and release the pedals. Keep still for a moment, then move the input you want and return it to rest."
        End If
        status.Text = If(source.SelectedIndex = 1, "Ready", "Waiting for a stable rest…")
    End Sub

    Private Sub NextStep(binding As DrivingBinding)
        If wizard.Action Is Nothing Then Return
        wizard.Record(binding)
        If wizard.Done Then Finish() Else BeginStep()
    End Sub

    ' Finish, Cancel and the last batch all end here; finished batches are kept.
    Private Sub Finish()
        wizard.Finish()
        DialogResult = If(wizard.FinishedBatches > 0, DialogResult.OK, DialogResult.Cancel) : Close()
    End Sub

    Protected Overrides Function ProcessCmdKey(ByRef msg As Message, keyData As Keys) As Boolean
        Dim action = wizard.Action
        If source.SelectedIndex <> 1 OrElse action Is Nothing Then Return MyBase.ProcessCmdKey(msg, keyData)
        If held <> Keys.None Then Return True
        Dim key = keyData And Keys.KeyCode
        If key = Keys.Tab Then Return MyBase.ProcessCmdKey(msg, keyData)
        key = If(key = Keys.ShiftKey, Keys.LShiftKey, If(key = Keys.ControlKey, Keys.LControlKey, If(key = Keys.Menu, Keys.LMenu, key)))
        Dim native = DrivingControls.KeyInput(key)
        If native Is Nothing Then status.Text = "That key can't be bound. Try another." : Return True
        held = key : releaseAt = -1
        pendingKey = New DrivingBinding With {.Action = action, .Device = "Keyboard", .DeviceType = "keyboard", .DeviceId = "keyboard", .Input = native}
        status.Text = DrivingCapture.Description(pendingKey) & " — release to continue."
        Return True
    End Function

    Private Sub Poll()
        Dim action = wizard.Action
        If Not ContainsFocus OrElse action Is Nothing Then Return
        Dim now = Environment.TickCount64
        If source.SelectedIndex = 1 Then
            If held = Keys.None Then Return
            If GetAsyncKeyState(CInt(held)) < 0 Then releaseAt = -1 : Return
            If releaseAt < 0 Then releaseAt = now
            If now - releaseAt >= 160 Then NextStep(pendingKey)
            Return
        End If
        If input Is Nothing OrElse input.Devices.Count = 0 Then status.Text = If(inputFailure, "Connect a device and Refresh, or choose Keyboard.") : Return
        Dim chosen = If(allDevices.Checked, Nothing, TryCast(devices.SelectedItem, DrivingInput.Device))
        If Not allDevices.Checked AndAlso chosen Is Nothing Then status.Text = "Select a device, or detect inputs from all devices." : Return
        Dim ready = 0, axesReady = 0, connected = 0
        For Each device In input.Devices
            If chosen IsNot Nothing AndAlso chosen.Id <> device.Id Then Continue For
            Dim capture As DrivingCapture = Nothing
            If Not captures.TryGetValue(device.Id, capture) Then
                capture = New DrivingCapture(device, action) With {.AxesOnly = filter.SelectedIndex = 1, .ButtonsOnly = filter.SelectedIndex = 2}
                captures(device.Id) = capture
            End If
            Dim sample = input.Read(device)
            capture.Update(sample, now)
            If sample.Connected <> 0UI Then connected += 1
            If capture Is active AndAlso sample.Connected = 0UI Then BeginStep() : status.Text = "Device disconnected. Reconnect it and Refresh." : Return
            If capture.Ready Then ready += 1
            If capture.AxesReady Then axesReady += 1
        Next
        ' Every device is sampled before choosing, so a button press wins over pedal noise.
        active = DrivingCapture.ChooseActive(captures.Values, active)
        If active IsNot Nothing Then
            status.Text = DrivingCapture.Description(active.Detected) & " — release / return to rest."
            If active.Completed IsNot Nothing Then NextStep(active.Completed)
        ElseIf ready > 0 Then
            status.Text = If(filter.SelectedIndex = 0 AndAlso axesReady = 0, "Buttons and hats ready — keep axes still briefly.", "Ready — move an axis or press a button.")
        Else
            status.Text = If(connected > 0, "Waiting for a stable rest…", "Device disconnected. Reconnect it and Refresh.")
        End If
    End Sub

    Protected Overrides Sub OnDeactivate(e As EventArgs)
        MyBase.OnDeactivate(e)
        BeginStep()
    End Sub

    Protected Overrides Sub Dispose(disposing As Boolean)
        If disposing Then pollTimer.Dispose() : input?.Dispose()
        MyBase.Dispose(disposing)
    End Sub
End Class

' Wheel rotation…: your wheel's rotation lock to lock and the rotation you want in DiRT 3. Apply sets Steer
' Left and Steer Right's saturation to their ratio (DrivingControls.ApplyWheelRotation).
Public NotInheritable Class WheelRotationForm
    Inherits LauncherForm

    Private ReadOnly model As DrivingControls
    Private ReadOnly wheel As New NumericUpDown With {.Name = "WheelRotationHardware", .AccessibleName = "Your wheel's rotation in degrees", .Minimum = DrivingControls.MinimumWheelRotation, .Maximum = DrivingControls.MaximumWheelRotation, .Width = 92, .Anchor = AnchorStyles.Right}
    Private ReadOnly game As New NumericUpDown With {.Name = "WheelRotationGame", .AccessibleName = "Steering rotation in DiRT 3 in degrees", .Minimum = DrivingControls.MinimumGameRotation, .Maximum = DrivingControls.MaximumWheelRotation, .Width = 92, .Anchor = AnchorStyles.Right}
    Private ReadOnly result As New Label With {.Name = "WheelRotationResult", .AutoSize = True, .MaximumSize = New Size(600, 0), .Margin = New Padding(3, 8, 3, 8), .UseMnemonic = False}
    Private ReadOnly problem As New Label With {.Name = "WheelRotationProblem", .AutoSize = True, .MaximumSize = New Size(600, 0), .UseMnemonic = False}

    Public Sub New(model As DrivingControls)
        Me.model = model
        Dim steering = model.SteeringWheel()
        If steering Is Nothing Then Throw New IOException("Bind Steer Left and Steer Right to your wheel first, with the Binding wizard or Device….")
        Dim device = steering(0).Device, suggested = DrivingControls.SuggestedWheelRotation(device)
        Dim wheelValue = If(model.WheelRotation <> 0, model.WheelRotation, If(suggested <> 0, suggested, 900))
        wheel.Value = wheelValue
        game.Maximum = wheelValue : game.Value = Math.Clamp(CInt(Math.Round(steering.Max(Function(b) b.Saturation) * wheelValue)), DrivingControls.MinimumGameRotation, wheelValue)
        Text = "Wheel rotation" : ClientSize = New Size(650, 470) : MinimumSize = Size
        StartPosition = FormStartPosition.CenterParent : AutoScaleMode = AutoScaleMode.Dpi : MinimizeBox = False : MaximizeBox = False
        Dim note = If(suggested <> 0, "Suggested: " & suggested.ToString(CultureInfo.CurrentCulture) & "°, this wheel's maximum. If you lowered it in the wheel's software, use that value.",
                      If(device.ToUpperInvariant().Contains("FANATEC"), "Use the SEN value from your wheelbase's tuning menu.", "Use the rotation set in your wheel's software."))
        Dim layout As New TableLayoutPanel With {.Dock = DockStyle.Fill, .Padding = New Padding(20), .ColumnCount = 1, .RowCount = 9, .AutoScroll = True}
        layout.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 100))
        For i = 0 To 8
            layout.RowStyles.Add(New RowStyle(SizeType.AutoSize))
        Next
        Dim caption = Function(value As String, top As Integer) New Label With {.Text = value, .AutoSize = True, .MaximumSize = New Size(600, 0), .Margin = New Padding(3, top, 3, 3), .UseMnemonic = False}
        layout.Controls.Add(caption("Wheel: " & device, 0), 0, 0)
        layout.Controls.Add(caption("Your wheel's rotation, lock to lock (°)", 10), 0, 1) : layout.Controls.Add(Slider(wheel), 0, 2)
        layout.Controls.Add(caption(note, 0), 0, 3)
        layout.Controls.Add(caption("Steering rotation in DiRT 3, lock to lock (°)", 10), 0, 4) : layout.Controls.Add(Slider(game), 0, 5)
        layout.Controls.Add(result, 0, 6)
        layout.Controls.Add(caption("The cockpit wheel can't turn further than each car's animation, so lower DiRT 3's rotation until it keeps up with your hands. If your wheel's software can set its rotation, setting it to the same value also stops the wheel at full lock.", 0), 0, 7)
        Dim apply As New Button With {.Name = "WheelRotationApply", .Text = "Apply", .AutoSize = True}
        AddHandler apply.Click, Sub() ApplyRotation()
        Dim cancel As New Button With {.Text = "Cancel", .AutoSize = True, .DialogResult = DialogResult.Cancel}
        CancelButton = cancel
        Dim buttons As New FlowLayoutPanel With {.Dock = DockStyle.Fill, .AutoSize = True, .WrapContents = True}
        buttons.Controls.AddRange({apply, cancel, problem}) : layout.Controls.Add(buttons, 0, 8)
        Controls.Add(layout)
        AddHandler wheel.ValueChanged, Sub()
                                           game.Maximum = wheel.Value
                                           ShowResult()
                                       End Sub
        AddHandler game.ValueChanged, Sub() ShowResult()
        ShowResult()
    End Sub

    ' Applies the two rotations to the edited bindings; the controls window saves them with Save.
    Public Sub ApplyRotation()
        Try
            model.ApplyWheelRotation(CInt(wheel.Value), CInt(game.Value))
            DialogResult = DialogResult.OK : Close()
        Catch ex As IOException
            problem.Text = ex.Message
        End Try
    End Sub

    Private Sub ShowResult()
        result.Text = "Full steering lock at " & CInt(game.Value).ToString(CultureInfo.CurrentCulture) & "° lock to lock: " &
            Math.Round(game.Value / wheel.Value, 2).ToString("P0", CultureInfo.CurrentCulture) & " of your wheel's travel (Steer Left and Steer Right saturation)."
    End Sub

    ' A slider beside the number box; the slider follows the box's range when it changes.
    Private Shared Function Slider(number As NumericUpDown) As Control
        Dim track As New TrackBar With {.Name = number.Name & "Slider", .AccessibleName = number.AccessibleName, .Minimum = CInt(number.Minimum), .Maximum = CInt(number.Maximum),
                                        .Value = CInt(number.Value), .SmallChange = 10, .LargeChange = 90, .TickStyle = TickStyle.None, .AutoSize = False, .Dock = DockStyle.Fill, .Margin = Padding.Empty}
        Dim panel As New TableLayoutPanel With {.Dock = DockStyle.Fill, .Height = 38, .MinimumSize = New Size(240, 38), .ColumnCount = 2, .RowCount = 1, .Margin = Padding.Empty}
        panel.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 100)) : panel.ColumnStyles.Add(New ColumnStyle(SizeType.AutoSize))
        panel.RowStyles.Add(New RowStyle(SizeType.Percent, 100))
        panel.Controls.Add(track) : panel.Controls.Add(number)
        Dim synchronizing = False
        AddHandler track.ValueChanged, Sub()
                                           If Not synchronizing Then number.Value = track.Value
                                       End Sub
        AddHandler number.ValueChanged, Sub()
                                            synchronizing = True
                                            Try
                                                track.Maximum = CInt(number.Maximum) : track.Value = CInt(number.Value)
                                            Finally
                                                synchronizing = False
                                            End Try
                                        End Sub
        Return panel
    End Function
End Class
