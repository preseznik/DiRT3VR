Imports System.Globalization
Imports System.Text.Json
Imports System.Text.Json.Serialization
Imports System.Text.RegularExpressions
Imports System.Windows.Forms

' One launcher binding: a DiRT 3 action on one input of one device, with DiRT 3's
' CalibratorSimple settings. Action, input and calibrator names are DiRT 3's own, from its
' stock actionmap/*.xml (docs/research/driving-controls.md, tools/data/action-names.json,
' tools/data/stock-bindings.json). Ported from GRID2VR's launcher/DrivingControls.cs.
Public NotInheritable Class DrivingBinding
    Public Property Action As String = ""
    ' DirectInput instance GUID, "xinput:0".."xinput:3" or "keyboard".
    Public Property DeviceId As String = ""
    ' DirectInput product name (what DiRT 3's action maps match as deviceName), "Xbox controller N" or "Keyboard".
    Public Property Device As String = ""
    ' DirectInput product GUID, "xinput_pad" or "keyboard".
    Public Property DeviceType As String = ""
    Public Property Input As String = ""
    Public Property Calibration As String = "uniDirectionalPositive"
    Public Property DeadZone As Decimal
    Public Property Saturation As Decimal = 1D

    <JsonIgnore>
    Public ReadOnly Property Keyboard As Boolean
        Get
            Return DeviceType = "keyboard"
        End Get
    End Property

    Public Function Copy() As DrivingBinding
        Return DirectCast(MemberwiseClone(), DrivingBinding)
    End Function

    Public Function SameAs(other As DrivingBinding) As Boolean
        Return other IsNot Nothing AndAlso Action = other.Action AndAlso DeviceId = other.DeviceId AndAlso Device = other.Device AndAlso DeviceType = other.DeviceType AndAlso
            Input = other.Input AndAlso Calibration = other.Calibration AndAlso DeadZone = other.DeadZone AndAlso Saturation = other.Saturation
    End Function
End Class

Public NotInheritable Class DrivingWizardBatch
    Public ReadOnly Property Name As String
    Public ReadOnly Property Summary As String
    Public ReadOnly Property Actions As String()

    Public Sub New(name As String, summary As String, actions As String())
        Me.Name = name : Me.Summary = summary : Me.Actions = actions
    End Sub
End Class

' The player's launcher bindings. Device identities are machine-specific, so they live in
' driving-controls.json beside the launcher settings, never in settings profiles; driving
' presets are separate files of the same format (Save / Load in the controls window).
Public NotInheritable Class DrivingControls
    Public Const MaxDevices As Integer = 16
    Public Const MaxFileBytes As Integer = 524288

    Public Property Version As Integer = 1
    Public Property Enabled As Boolean
    Public Property ListenAllDevices As Boolean = True
    Public Property Bindings As New List(Of DrivingBinding)

    ' DiRT 3's driving, menu and replay actions (its action maps hold 72; the rest are
    ' YouTube, spectator, front-end camera and test actions).
    Public Shared ReadOnly Actions As String() = {"Steer Left", "Steer Right", "Accelerate", "Brake", "Clutch", "Handbrake",
        "Gear Up", "Gear Down", "Gear 1", "Gear 2", "Gear 3", "Gear 4", "Gear 5", "Gear 6", "Gear Reverse",
        "Change View", "Look Back", "Look Left", "Look Right", "Look Up", "Look Down", "Horn", "Pause", "Instant Replay",
        "Menu Start Button", "Menu Select", "Menu Back", "Menu Up", "Menu Down", "Menu Left", "Menu Right",
        "Replay Rewind", "Replay Fast Forward", "Replay Pause", "Replay Next Camera", "Replay Prev Camera", "Replay Jump In", "Replay Exit", "Replay UI On Off"}

    ' The wizard binds Essentials, then asks before each later batch. Together the batches
    ' cover every action exactly once.
    Public Shared ReadOnly WizardBatches As DrivingWizardBatch() = {
        New DrivingWizardBatch("Essentials", "steering, pedals, handbrake, gear up/down, camera and pause",
            {"Steer Left", "Steer Right", "Accelerate", "Brake", "Clutch", "Handbrake", "Gear Up", "Gear Down", "Change View", "Look Back", "Pause"}),
        New DrivingWizardBatch("H-pattern shifter", "Gear 1–6 and Reverse", {"Gear 1", "Gear 2", "Gear 3", "Gear 4", "Gear 5", "Gear 6", "Gear Reverse"}),
        New DrivingWizardBatch("Flashback and replays", "Flashback (Instant Replay) and the replay viewer",
            {"Instant Replay", "Replay Pause", "Replay Rewind", "Replay Fast Forward", "Replay Next Camera", "Replay Prev Camera", "Replay Jump In", "Replay Exit", "Replay UI On Off"}),
        New DrivingWizardBatch("Menu controls", "menu navigation, select, back and start",
            {"Menu Up", "Menu Down", "Menu Left", "Menu Right", "Menu Select", "Menu Back", "Menu Start Button"}),
        New DrivingWizardBatch("Look and horn", "look left/right/up/down and horn", {"Look Left", "Look Right", "Look Up", "Look Down", "Horn"})}

    Public Shared ReadOnly Calibrations As String() = {"uniDirectionalPositive", "uniDirectionalNegative", "biDirectionalLower", "biDirectionalUpper"}

    ' DiRT 3's own wheel presets (lg_g25, lg_g27 and their JPN twins) give the clutch a 10% dead zone and
    ' full output at 80% travel; at 0% / 100% a G920's clutch didn't shift in H-pattern with clutch
    ' (#16fa2aa3). Other axes start at 0% / 100% (#e7547282).
    Public Const ClutchDeadZone As Decimal = 0.1D, ClutchSaturation As Decimal = 0.8D

    ' The dead zone and saturation an axis bound to the action starts with.
    Public Shared Function DefaultCalibration(action As String) As (DeadZone As Decimal, Saturation As Decimal)
        Return If(action = "Clutch", (ClutchDeadZone, ClutchSaturation), (0D, 1D))
    End Function
    Public Shared ReadOnly XboxAxes As String() = {"xinput_left_stick_x", "xinput_left_stick_y", "xinput_right_stick_x", "xinput_right_stick_y", "xinput_left_trigger", "xinput_right_trigger"}
    Public Shared ReadOnly DirectAxes As String() = {"di_x_axis", "di_y_axis", "di_z_axis", "di_x_axis_rotation", "di_y_axis_rotation", "di_z_axis_rotation", "di_slider_0", "di_slider_1"}
    ' XInput wButtons bit order (DrivingInput.XboxButtonNames); bits 10 and 11 are unused.
    Public Shared ReadOnly XboxButtons As String() = {"xinput_dpad_up", "xinput_dpad_down", "xinput_dpad_left", "xinput_dpad_right", "xinput_button_start", "xinput_button_back",
        "xinput_button_left_stick", "xinput_button_right_stick", "xinput_button_left_shoulder", "xinput_button_right_shoulder", "", "", "xinput_button_a", "xinput_button_b", "xinput_button_x", "xinput_button_y"}

    Public Shared Function SettingsPath(context As InstallContext) As String
        Return Path.Combine(context.UserRoot, "driving-controls.json")
    End Function

    Public Shared Function PresetsPath(context As InstallContext) As String
        Return Path.Combine(context.UserRoot, "driving-presets")
    End Function

    Public Shared Function Load(context As InstallContext) As DrivingControls
        Dim file = SettingsPath(context)
        Return If(IO.File.Exists(file), LoadFile(file), New DrivingControls())
    End Function

    Public Shared Function LoadFile(file As String) As DrivingControls
        SafeFiles.NoLinks(file)
        Dim bytes As Byte()
        Using stream = IO.File.OpenRead(file)
            If stream.Length < 1 OrElse stream.Length > MaxFileBytes Then Throw New IOException("The driving controls file is empty or too large.")
            bytes = New Byte(CInt(stream.Length) - 1) {}
            stream.ReadExactly(bytes)
            If stream.ReadByte() <> -1 Then Throw New IOException("The driving controls file changed while reading.")
        End Using
        If bytes.Length >= 3 AndAlso bytes(0) = &HEF AndAlso bytes(1) = &HBB AndAlso bytes(2) = &HBF Then bytes = bytes.Skip(3).ToArray()
        Dim value As DrivingControls
        Try
            value = JsonSerializer.Deserialize(Of DrivingControls)(bytes, SafeFiles.JsonOptions)
        Catch ex As JsonException
            Throw New IOException("The driving controls file is not valid JSON.", ex)
        End Try
        If value Is Nothing Then Throw New IOException("The driving controls file is invalid.")
        value.Validate()
        ' Earlier launchers bound a clutch axis at 0% / 100%; it takes the clutch's own default.
        For Each binding In value.Bindings
            If binding.Action = "Clutch" AndAlso DrivingCapture.IsAxis(binding) AndAlso binding.DeadZone = 0D AndAlso binding.Saturation = 1D Then
                binding.DeadZone = ClutchDeadZone : binding.Saturation = ClutchSaturation
            End If
        Next
        Return value
    End Function

    Public Sub Save(context As InstallContext)
        SaveFile(SettingsPath(context))
    End Sub

    Public Sub SaveFile(file As String)
        Validate()
        Dim found = Problems()
        If Enabled AndAlso found.Count > 0 Then Throw New IOException(String.Join(Environment.NewLine, found))
        Dim bytes = JsonSerializer.SerializeToUtf8Bytes(New DrivingControls With {.Enabled = Enabled, .ListenAllDevices = ListenAllDevices, .Bindings = Bindings.Select(Function(b) b.Copy()).ToList()}, SafeFiles.JsonOptions)
        If bytes.Length > MaxFileBytes Then Throw New IOException("The driving controls file is too large.")
        SafeFiles.WriteAtomic(Path.GetFullPath(file), bytes)
    End Sub

    Public Sub Validate()
        If Version <> 1 OrElse Bindings Is Nothing OrElse Bindings.Count > Actions.Length * MaxDevices Then Throw New IOException("The driving controls are invalid.")
        Dim slots As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
        Dim families As New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase)
        For Each binding In Bindings
            If binding Is Nothing OrElse Not Actions.Contains(binding.Action) OrElse Not Calibrations.Contains(binding.Calibration) OrElse
                String.IsNullOrWhiteSpace(binding.Device) OrElse binding.Device.Length > 260 OrElse binding.Device.Any(Function(c) Char.IsControl(c)) OrElse
                String.IsNullOrWhiteSpace(binding.DeviceId) OrElse binding.DeviceId.Length > 128 OrElse binding.DeviceId.Any(Function(c) Char.IsControl(c)) OrElse
                binding.DeadZone < 0D OrElse binding.DeadZone > 0.9D OrElse binding.Saturation < 0.1D OrElse binding.Saturation > 1D OrElse binding.DeadZone >= binding.Saturation OrElse
                CSng(binding.DeadZone) >= CSng(binding.Saturation) OrElse Not ValidInput(binding) Then
                Throw New IOException("A driving binding is invalid. Check its device, input and calibration.")
            End If
            If Not slots.Add(binding.DeviceId & "/" & binding.Action) Then Throw New IOException("Two bindings for " & binding.Action & " on " & binding.Device & ".")
            Dim family As String = Nothing
            If families.TryGetValue(binding.DeviceId, family) AndAlso Not String.Equals(family, binding.DeviceType, StringComparison.OrdinalIgnoreCase) Then
                Throw New IOException("A device has conflicting controller types.")
            End If
            families(binding.DeviceId) = binding.DeviceType
        Next
        If families.Count > MaxDevices Then Throw New IOException("Driving controls support up to " & MaxDevices.ToString(CultureInfo.InvariantCulture) & " devices.")
    End Sub

    Private Shared Function ValidInput(binding As DrivingBinding) As Boolean
        If binding.Input Is Nothing OrElse binding.DeviceType Is Nothing Then Return False
        If binding.Keyboard Then
            Dim code As Integer
            Return binding.DeviceId = "keyboard" AndAlso Regex.IsMatch(binding.Input, "\Avk_code_0x[0-9A-F]{2}\z") AndAlso
                Integer.TryParse(binding.Input.AsSpan(10), NumberStyles.HexNumber, CultureInfo.InvariantCulture, code) AndAlso code >= 8 AndAlso code <= 254
        End If
        If binding.DeviceType = "xinput_pad" Then
            Return Regex.IsMatch(binding.DeviceId, "\Axinput:[0-3]\z") AndAlso (XboxAxes.Contains(binding.Input) OrElse (binding.Input.Length > 0 AndAlso XboxButtons.Contains(binding.Input)))
        End If
        Dim parsed As Guid
        Return binding.DeviceType.Length = 38 AndAlso binding.DeviceId.Length = 38 AndAlso Guid.TryParseExact(binding.DeviceType, "B", parsed) AndAlso Guid.TryParseExact(binding.DeviceId, "B", parsed) AndAlso
            (DirectAxes.Contains(binding.Input) OrElse Regex.IsMatch(binding.Input, "\Adi_dpad_[0-3]_(up|down|left|right)\z") OrElse
             Regex.IsMatch(binding.Input, "\Adi_button_([0-9]|[1-9][0-9]|1[01][0-9]|12[0-7])\z"))
    End Function

    Public Function Problems() As List(Of String)
        Dim result As New List(Of String)
        For Each device In Bindings.GroupBy(Function(b) b.DeviceId, StringComparer.OrdinalIgnoreCase)
            Dim left = device.FirstOrDefault(Function(b) b.Action = "Steer Left")
            Dim right = device.FirstOrDefault(Function(b) b.Action = "Steer Right")
            If left IsNot Nothing AndAlso right IsNot Nothing AndAlso left.Input = right.Input AndAlso left.Calibration = right.Calibration Then
                result.Add("Steer Left and Steer Right on " & left.Device & " use the same direction. Bind steering again.")
            End If
        Next
        For Each binding In Bindings
            If binding.DeviceType = "xinput_pad" AndAlso binding.Input.EndsWith("_trigger", StringComparison.Ordinal) AndAlso binding.Calibration <> "uniDirectionalPositive" Then
                result.Add(binding.Action & ": Xbox triggers must increase when pressed.")
            End If
        Next
        Return result
    End Function

    ' One binding per action and device: replaces that device's binding for the action and
    ' keeps the action's bindings on other devices.
    Public Sub Assign(binding As DrivingBinding)
        Bindings.RemoveAll(Function(b) b.Action = binding.Action AndAlso String.Equals(b.DeviceId, binding.DeviceId, StringComparison.OrdinalIgnoreCase))
        Bindings.Add(binding)
    End Sub

    Public Sub ApplyXboxPreset()
        For Each binding In XboxPreset()
            Assign(binding)
        Next
    End Sub

    ' DiRT 3's own xinput_pad.xml for these actions (tools/data/stock-bindings.json), one input
    ' per action: the stick or trigger where the stock map has one, else its button.
    Public Shared Function XboxPreset() As List(Of DrivingBinding)
        Dim result As New List(Of DrivingBinding)
        Dim add = Sub(action As String, input As String, calibration As String, deadZone As Decimal, saturation As Decimal)
                      result.Add(New DrivingBinding With {.Action = action, .Device = "Xbox controller 1", .DeviceId = "xinput:0", .DeviceType = "xinput_pad",
                                 .Input = "xinput_" & input, .Calibration = calibration, .DeadZone = deadZone, .Saturation = saturation})
                  End Sub
        Dim button = Sub(action As String, input As String) add(action, input, "uniDirectionalPositive", 0D, 1D)
        add("Steer Left", "left_stick_x", "biDirectionalLower", 0.2D, 1D) : add("Steer Right", "left_stick_x", "biDirectionalUpper", 0.2D, 1D)
        add("Accelerate", "right_trigger", "uniDirectionalPositive", 0D, 1D) : add("Brake", "left_trigger", "uniDirectionalPositive", 0D, 1D)
        button("Handbrake", "button_b") : button("Gear Up", "button_x") : button("Gear Down", "button_a")
        button("Change View", "button_right_shoulder") : button("Look Back", "button_y")
        add("Look Left", "right_stick_x", "biDirectionalLower", 0.3D, 0.8D) : add("Look Right", "right_stick_x", "biDirectionalUpper", 0.3D, 0.8D)
        add("Look Up", "right_stick_y", "biDirectionalUpper", 0.3D, 0.8D) : add("Look Down", "right_stick_y", "biDirectionalLower", 0.3D, 0.8D)
        button("Horn", "button_right_stick") : button("Pause", "button_start") : button("Instant Replay", "button_left_shoulder")
        button("Menu Start Button", "button_start") : button("Menu Select", "button_a") : button("Menu Back", "button_b")
        For Each direction In {"Up", "Down", "Left", "Right"}
            button("Menu " & direction, "dpad_" & direction.ToLowerInvariant())
        Next
        add("Replay Rewind", "left_trigger", "uniDirectionalPositive", 0D, 1D) : add("Replay Fast Forward", "right_trigger", "uniDirectionalPositive", 0D, 1D)
        button("Replay Pause", "button_a") : button("Replay Next Camera", "button_right_shoulder") : button("Replay Prev Camera", "button_left_shoulder")
        button("Replay Jump In", "button_x") : button("Replay Exit", "button_b") : button("Replay UI On Off", "button_back")
        Return result
    End Function

    ' DiRT 3's keyboard input name for a key (vk_code_0x26 = Up arrow), or Nothing.
    Public Shared Function KeyInput(key As Keys) As String
        Dim value = CInt(key And Keys.KeyCode)
        If value < 8 OrElse value > 254 OrElse key = Keys.LWin OrElse key = Keys.RWin OrElse key = Keys.Apps OrElse (key And Keys.Modifiers) <> 0 Then Return Nothing
        Return "vk_code_0x" & value.ToString("X2", CultureInfo.InvariantCulture)
    End Function
End Class

' Binding wizard progress, one batch at a time. Every batch after the first waits for Accept
' or Skip. Only finished batches are kept, so a skipped, cancelled or unfinished batch leaves
' that batch's current bindings untouched.
Public NotInheritable Class DrivingWizard
    Private ReadOnly kept As New List(Of DrivingBinding)
    Private ReadOnly pending As New List(Of DrivingBinding)

    Public ReadOnly Property Batches As IReadOnlyList(Of DrivingWizardBatch)
    Public ReadOnly Property BatchIndex As Integer
    Public ReadOnly Property [Step] As Integer
    Public ReadOnly Property FinishedBatches As Integer
    Public ReadOnly Property Asking As Boolean
    Public ReadOnly Property Done As Boolean

    Public Sub New(batches As IReadOnlyList(Of DrivingWizardBatch))
        If batches Is Nothing OrElse batches.Count = 0 OrElse batches.Any(Function(b) b.Actions.Length = 0) Then Throw New ArgumentException("The binding wizard needs actions.", NameOf(batches))
        Me.Batches = batches
    End Sub

    Public ReadOnly Property Batch As DrivingWizardBatch
        Get
            Return Batches(_BatchIndex)
        End Get
    End Property

    Public ReadOnly Property Action As String
        Get
            Return If(_Asking OrElse _Done, Nothing, Batch.Actions(_Step))
        End Get
    End Property

    Public ReadOnly Property Bindings As IReadOnlyList(Of DrivingBinding)
        Get
            Return kept
        End Get
    End Property

    Public Sub Record(binding As DrivingBinding)
        Dim current = Action
        If current Is Nothing Then Throw New InvalidOperationException("The binding wizard is not capturing.")
        If binding IsNot Nothing AndAlso binding.Action <> current Then Throw New InvalidOperationException("The binding does not match the wizard step.")
        If binding IsNot Nothing Then pending.Add(binding)
        _Step += 1
        If _Step < Batch.Actions.Length Then Return
        kept.AddRange(pending) : pending.Clear() : _FinishedBatches += 1 : Advance()
    End Sub

    Public Sub Accept()
        If Not _Asking Then Throw New InvalidOperationException("The binding wizard is not asking.")
        _Asking = False
    End Sub

    Public Sub Skip()
        If Not _Asking Then Throw New InvalidOperationException("The binding wizard is not asking.")
        Advance()
    End Sub

    Public Sub Finish()
        pending.Clear() : _Asking = False : _Done = True
    End Sub

    Private Sub Advance()
        _Step = 0
        If _BatchIndex + 1 < Batches.Count Then
            _BatchIndex += 1 : _Asking = True
        Else
            _Asking = False : _Done = True
        End If
    End Sub
End Class
