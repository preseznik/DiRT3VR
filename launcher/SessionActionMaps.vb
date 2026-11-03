Imports System.Globalization
Imports System.Text
Imports System.Text.RegularExpressions
Imports System.Xml

' The launcher's driving bindings in a direct practice session (#8121970e). Wrench's runs 93–94
' (docs/research/driving-controls.md): an added action map with the same deviceName as a stock map and a
' higher priority, listed after the stock maps in actionmap\actionMapPaths.xml, is used instead of the
' stock map for that device; it replaces that map rather than merging with it, and an edited stock file
' isn't picked up. So every bound device gets one complete map: DiRT 3's loaded map for that device
' (matched by deviceName; else dinput_default for DirectInput devices), with the launcher's bindings
' replacing those actions. The map files are created for the session and actionMapPaths.xml lists them
' after the stock maps; the recovery journal removes both changes after DiRT 3 exits. Direct practice
' only: a normal start loads the profile, whose own bindings may still win (not tested yet).
Public NotInheritable Class SessionActionMaps
    Public Const PathsTarget As String = "actionpaths"
    Public Const MapTargetPrefix As String = "actionmap/"
    Public Const Priority As Integer = 100
    Public Const MaxMaps As Integer = DrivingControls.MaxDevices
    Private Const MaxMapBytes As Integer = 262144
    Private Const DefaultDirectInput As String = "di_device_default"
    Private Const Comment As String = "<!-- DiRT3VR: the launcher's driving bindings for one device; removed after the session -->"

    Private Shared ReadOnly MapHeader As New Regex("<ActionMap\s+actionMapName=""([^""]*)""\s+deviceName=""([^""]*)""\s+priority=""([^""]*)""", RegexOptions.CultureInvariant)
    Private Shared ReadOnly ActionBlock As New Regex("<Action\s+actionName=""([^""]+)""\s*>(.*?)</Action>", RegexOptions.CultureInvariant Or RegexOptions.Singleline)
    Private Shared ReadOnly AxisBlock As New Regex("<Axis\s+axisName=""([^""]+)""\s*>(.*?)</Axis>", RegexOptions.CultureInvariant Or RegexOptions.Singleline)
    Private Shared ReadOnly CalibratorTag As New Regex("<CalibratorSimple\s+([^/>]*)/?>", RegexOptions.CultureInvariant)
    Private Shared ReadOnly AttributePair As New Regex("(\w+)=""([^""]*)""", RegexOptions.CultureInvariant)
    Private Shared ReadOnly CommentBlock As New Regex("<!--.*?-->", RegexOptions.CultureInvariant Or RegexOptions.Singleline)
    Private Shared ReadOnly ListedMap As New Regex("<xmlreader\s[^>]*processor=""ActionMap2""[^>]*>", RegexOptions.CultureInvariant)
    Private Shared ReadOnly ListedFile As New Regex("filename=""(actionmap/[^""]+)""", RegexOptions.CultureInvariant)
    Private Shared ReadOnly SafeName As New Regex("\A[ -~]{1,260}\z", RegexOptions.CultureInvariant)
    Private Shared ReadOnly SafeAction As New Regex("\A[A-Za-z0-9 ]{1,64}\z", RegexOptions.CultureInvariant)
    Private Shared ReadOnly SafeAxis As New Regex("\A[A-Za-z0-9_]{1,64}\z", RegexOptions.CultureInvariant)
    Private Shared ReadOnly SafeNumber As New Regex("\A[0-9]{1,3}(\.[0-9]{1,8})?\z", RegexOptions.CultureInvariant)

    Private Sub New()
    End Sub

    ' One input of an action, with its CalibratorSimple attributes as written (Nothing when absent).
    Public NotInheritable Class MapAxis
        Public Property AxisName As String = ""
        Public Property Type As String
        Public Property DeadZone As String
        Public Property Saturation As String
    End Class

    Public NotInheritable Class MapAction
        Public Property Name As String = ""
        Public Property Axes As New List(Of MapAxis)
    End Class

    Public NotInheritable Class ActionMapFile
        Public Property File As String = ""
        Public Property Name As String = ""
        Public Property DeviceName As String = ""
        Public Property Priority As Integer
        Public Property Actions As New List(Of MapAction)
    End Class

    Public NotInheritable Class SessionChanges
        Public ReadOnly Property Files As New Dictionary(Of String, Byte())(StringComparer.Ordinal)
        ' deviceName → the stock map file each session map was built from.
        Public ReadOnly Property Devices As New List(Of (DeviceName As String, BaseFile As String))
        Public ReadOnly Property Skipped As New List(Of String)
        ' The devices DiRT 3 should give the player in a direct start, best first (PreferredDevices).
        Public Property PreferredDevices As String() = Array.Empty(Of String)()

        ' For the launch notice: the devices whose bindings couldn't be applied, or "".
        Public ReadOnly Property Note As String
            Get
                Return If(Skipped.Count = 0, "", "Launcher driving bindings not applied for " & String.Join(" ", Skipped))
            End Get
        End Property
    End Class

    Public Shared Function MapFileName(index As Integer) As String
        Return "dirt3vr-controls-" & index.ToString(CultureInfo.InvariantCulture) & ".xml"
    End Function

    Public Shared Function MapTarget(index As Integer) As String
        Return MapTargetPrefix & index.ToString(CultureInfo.InvariantCulture)
    End Function

    Public Shared Function IsMapTarget(target As String) As Boolean
        Return MapIndex(target) > 0
    End Function

    ' 1..MaxMaps for "actionmap/N", else 0.
    Public Shared Function MapIndex(target As String) As Integer
        If target Is Nothing OrElse Not target.StartsWith(MapTargetPrefix, StringComparison.Ordinal) Then Return 0
        Dim digits = target.Substring(MapTargetPrefix.Length)
        Dim index As Integer
        If Not Regex.IsMatch(digits, "\A[1-9][0-9]?\z") OrElse Not Integer.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, index) OrElse index > MaxMaps Then Return 0
        Return index
    End Function

    Public Shared Function PathsPath(context As InstallContext) As String
        Return Path.Combine(context.GameRoot, "actionmap", "actionMapPaths.xml")
    End Function

    Public Shared Function MapPath(context As InstallContext, index As Integer) As String
        Return Path.Combine(context.GameRoot, "actionmap", MapFileName(index))
    End Function

    ' The ActionMap2 files actionMapPaths.xml lists ("actionmap/x.xml"), in order, outside comments.
    Public Shared Function ListedMaps(paths As Byte()) As List(Of String)
        Dim text = CommentBlock.Replace(Encoding.Latin1.GetString(paths), "")
        Dim result As New List(Of String)
        For Each entry As Match In ListedMap.Matches(text)
            Dim file = ListedFile.Match(entry.Value)
            If file.Success Then result.Add(file.Groups(1).Value)
        Next
        Return result
    End Function

    ' A stock map, read leniently as DiRT 3 does (two stock files aren't well-formed XML); a port of
    ' Wrench's reader in tools/research/make_action_names.py, with comments left out.
    Public Shared Function ReadMap(file As String, data As Byte()) As ActionMapFile
        Dim text = CommentBlock.Replace(Encoding.UTF8.GetString(data), "")
        Dim header = MapHeader.Match(text)
        If Not header.Success Then Return Nothing
        Dim value As Integer
        Dim result As New ActionMapFile With {.File = file, .Name = header.Groups(1).Value, .DeviceName = header.Groups(2).Value,
                                              .Priority = If(Integer.TryParse(header.Groups(3).Value, NumberStyles.Integer, CultureInfo.InvariantCulture, value), value, 0)}
        For Each block As Match In ActionBlock.Matches(text)
            Dim action As New MapAction With {.Name = block.Groups(1).Value}
            For Each axisMatch As Match In AxisBlock.Matches(block.Groups(2).Value)
                Dim axis As New MapAxis With {.AxisName = axisMatch.Groups(1).Value}
                Dim calibrator = CalibratorTag.Match(axisMatch.Groups(2).Value)
                If calibrator.Success Then
                    For Each pair As Match In AttributePair.Matches(calibrator.Groups(1).Value)
                        Select Case pair.Groups(1).Value
                            Case "type" : axis.Type = pair.Groups(2).Value
                            Case "deadZone" : axis.DeadZone = pair.Groups(2).Value
                            Case "saturation" : axis.Saturation = pair.Groups(2).Value
                        End Select
                    Next
                End If
                action.Axes.Add(axis)
            Next
            result.Actions.Add(action)
        Next
        Return result
    End Function

    Private Shared Function Number(value As Decimal) As String
        Return value.ToString("0.0#######", CultureInfo.InvariantCulture)
    End Function

    ' One launcher binding as a map input. Digital inputs (keys, buttons, hats) leave dead zone and
    ' saturation out, as the stock maps do, unless the player calibrated them.
    Public Shared Function AxisOf(binding As DrivingBinding) As MapAxis
        Dim calibrated = DrivingCapture.IsAxis(binding) OrElse binding.DeadZone <> 0D OrElse binding.Saturation <> 1D
        Return New MapAxis With {.AxisName = binding.Input, .Type = binding.Calibration,
                                 .DeadZone = If(calibrated, Number(binding.DeadZone), Nothing), .Saturation = If(calibrated, Number(binding.Saturation), Nothing)}
    End Function

    ' DiRT 3's deviceName for a binding's device: its DirectInput product name, or the stock names of the
    ' keyboard and Xbox maps.
    Public Shared Function DeviceNameOf(binding As DrivingBinding) As String
        If binding.Keyboard Then Return "keyboard"
        If binding.DeviceType = "xinput_pad" Then Return "xinput_pad"
        Return binding.Device
    End Function

    ' The devices DiRT 3 should bind to the player in a direct start (#16fa2aa3: it binds the first device in
    ' its list, so the keyboard won over the G920). Best first: the device bound to steering, then the others
    ' by how many actions they carry. DiRT 3's names as in the session maps; never the keyboard. Passed to the
    ' renderer as DIRT3VR_DIRECT_DEVICES (Wrench's side moves the first match to the front).
    Public Shared Function PreferredDevices(controls As DrivingControls) As String()
        If controls Is Nothing OrElse Not controls.Enabled OrElse controls.Bindings Is Nothing Then Return Array.Empty(Of String)()
        Return controls.Bindings.Where(Function(b) Not b.Keyboard).GroupBy(Function(b) DeviceNameOf(b), StringComparer.Ordinal).
            Where(Function(g) g.Key <> "" AndAlso g.Key <> "keyboard" AndAlso WritableName(g.Key) AndAlso Not g.Key.Contains("|"c)).
            OrderByDescending(Function(g) g.Any(Function(b) b.Action = "Steer Left" OrElse b.Action = "Steer Right")).
            ThenByDescending(Function(g) g.Count()).ThenBy(Function(g) g.Key, StringComparer.Ordinal).
            Select(Function(g) g.Key).ToArray()
    End Function

    ' The device's complete map: the base map's actions in their order, each action the player bound
    ' replaced by the player's input, then bound actions the base map lacks.
    Public Shared Function Compose(base As ActionMapFile, deviceName As String, index As Integer, bindings As IEnumerable(Of DrivingBinding)) As ActionMapFile
        If index < 1 OrElse index > MaxMaps Then Throw New ArgumentOutOfRangeException(NameOf(index))
        Dim chosen As New Dictionary(Of String, DrivingBinding)(StringComparer.Ordinal)
        For Each binding In bindings
            If Not chosen.ContainsKey(binding.Action) Then chosen.Add(binding.Action, binding)
        Next
        Dim result As New ActionMapFile With {.Name = "dirt3vrControls" & index.ToString(CultureInfo.InvariantCulture), .DeviceName = deviceName, .Priority = Priority}
        Dim used As New HashSet(Of String)(StringComparer.Ordinal)
        For Each action In If(base?.Actions, New List(Of MapAction)())
            If Not used.Add(action.Name) Then Continue For
            Dim binding As DrivingBinding = Nothing
            If chosen.TryGetValue(action.Name, binding) Then
                result.Actions.Add(New MapAction With {.Name = action.Name, .Axes = New List(Of MapAxis) From {AxisOf(binding)}})
            Else
                result.Actions.Add(New MapAction With {.Name = action.Name, .Axes = action.Axes.Select(Function(a) New MapAxis With {.AxisName = a.AxisName, .Type = a.Type, .DeadZone = a.DeadZone, .Saturation = a.Saturation}).ToList()})
            End If
        Next
        For Each name In DrivingControls.Actions
            Dim binding As DrivingBinding = Nothing
            If Not used.Contains(name) AndAlso chosen.TryGetValue(name, binding) Then result.Actions.Add(New MapAction With {.Name = name, .Axes = New List(Of MapAxis) From {AxisOf(binding)}})
        Next
        Return result
    End Function

    ' The stock layout: CRLF lines, two-space indentation, attributes in the stock order.
    Public Shared Function Write(map As ActionMapFile) As Byte()
        CheckWritable(map)
        Dim text As New StringBuilder()
        text.Append(Comment).Append(vbCrLf)
        text.Append("<ActionMap actionMapName=""").Append(map.Name).Append(""" deviceName=""").Append(map.DeviceName).Append(""" priority=""").Append(map.Priority.ToString(CultureInfo.InvariantCulture)).Append(""">").Append(vbCrLf)
        For Each action In map.Actions
            text.Append("  <Action actionName=""").Append(action.Name).Append(""">").Append(vbCrLf)
            For Each axis In action.Axes
                text.Append("    <Axis axisName=""").Append(axis.AxisName).Append(""">").Append(vbCrLf)
                If axis.Type IsNot Nothing Then
                    text.Append("      <CalibratorSimple type=""").Append(axis.Type).Append("""")
                    If axis.DeadZone IsNot Nothing Then text.Append(" deadZone=""").Append(axis.DeadZone).Append("""")
                    If axis.Saturation IsNot Nothing Then text.Append(" saturation=""").Append(axis.Saturation).Append("""")
                    text.Append(" />").Append(vbCrLf)
                End If
                text.Append("    </Axis>").Append(vbCrLf)
            Next
            text.Append("  </Action>").Append(vbCrLf)
        Next
        text.Append("</ActionMap>").Append(vbCrLf)
        Dim bytes = Encoding.ASCII.GetBytes(text.ToString())
        If bytes.Length > MaxMapBytes Then Throw New IOException("A session action map is too large.")
        Return bytes
    End Function

    Private Shared Sub CheckWritable(map As ActionMapFile)
        If Not Regex.IsMatch(map.Name, "\Adirt3vrControls([1-9]|1[0-6])\z") OrElse Not WritableName(map.DeviceName) OrElse map.Priority <> Priority OrElse map.Actions.Count = 0 OrElse map.Actions.Count > 256 Then
            Throw New IOException("A session action map is invalid.")
        End If
        For Each action In map.Actions
            If Not SafeAction.IsMatch(action.Name) OrElse action.Axes.Count > 16 Then Throw New IOException("A session action map has an invalid action.")
            For Each axis In action.Axes
                If Not SafeAxis.IsMatch(axis.AxisName) OrElse (axis.Type IsNot Nothing AndAlso Not DrivingControls.Calibrations.Contains(axis.Type)) OrElse
                   (axis.Type Is Nothing AndAlso (axis.DeadZone IsNot Nothing OrElse axis.Saturation IsNot Nothing)) OrElse
                   (axis.DeadZone IsNot Nothing AndAlso Not ValidFraction(axis.DeadZone)) OrElse (axis.Saturation IsNot Nothing AndAlso Not ValidFraction(axis.Saturation)) Then
                    Throw New IOException("A session action map has an invalid input.")
                End If
            Next
        Next
    End Sub

    Private Shared Function ValidFraction(value As String) As Boolean
        Dim parsed As Double
        Return SafeNumber.IsMatch(value) AndAlso Double.TryParse(value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, parsed) AndAlso parsed >= 0 AndAlso parsed <= 1
    End Function

    ' A deviceName DiRT3VR can write as it is: printable ASCII without XML markup characters.
    Public Shared Function WritableName(name As String) As Boolean
        Return name IsNot Nothing AndAlso SafeName.IsMatch(name) AndAlso name.IndexOfAny({"&"c, "<"c, ">"c, """"c}) < 0 AndAlso name.Trim().Length > 0
    End Function

    ' Recovery's approved shape for a created session map: exactly what Write produces for its own
    ' content, under its own index.
    Public Shared Sub Validate(data As Byte(), index As Integer)
        If data Is Nothing OrElse data.Length = 0 OrElse data.Length > MaxMapBytes OrElse data.Any(Function(b) b > 127) Then Throw New IOException("A session action map is invalid.")
        Dim text = Encoding.ASCII.GetString(data)
        If Not text.StartsWith(Comment & vbCrLf, StringComparison.Ordinal) Then Throw New IOException("A session action map is invalid.")
        Dim document As New XmlDocument With {.XmlResolver = Nothing}
        Try
            Using reader = XmlReader.Create(New IO.StringReader(text), New XmlReaderSettings With {.DtdProcessing = DtdProcessing.Prohibit, .XmlResolver = Nothing})
                document.Load(reader)
            End Using
        Catch ex As XmlException
            Throw New IOException("A session action map is not valid XML.", ex)
        End Try
        Dim map = ReadMap("", data)
        If map Is Nothing OrElse map.Name <> "dirt3vrControls" & index.ToString(CultureInfo.InvariantCulture) Then Throw New IOException("A session action map is invalid.")
        If Not Write(map).SequenceEqual(data) Then Throw New IOException("A session action map does not match DiRT3VR's format.")
    End Sub

    ' actionMapPaths.xml listing the session maps 1..count right after its last ActionMap2 entry, with
    ' that entry's indentation and line ending (Wrench's run 94 listed its map the same way).
    Public Shared Function ListMaps(original As Byte(), count As Integer) As Byte()
        If count < 1 OrElse count > MaxMaps Then Throw New IOException("Invalid number of session action maps.")
        Dim text = Encoding.Latin1.GetString(original)
        If text.Contains("dirt3vr-controls-", StringComparison.OrdinalIgnoreCase) Then Throw New IOException("actionMapPaths.xml already lists DiRT3VR's action maps.")
        Dim comments = CommentBlock.Matches(text).Cast(Of Match)().ToList()
        Dim last As Match = Nothing
        For Each line As Match In Regex.Matches(text, "^([ \t]*)<xmlreader\s[^>\r\n]*processor=""ActionMap2""[^>\r\n]*>[ \t]*(\r?\n)", RegexOptions.Multiline Or RegexOptions.CultureInvariant)
            If Not comments.Any(Function(c) line.Index >= c.Index AndAlso line.Index < c.Index + c.Length) Then last = line
        Next
        If last Is Nothing Then Throw New IOException("actionMapPaths.xml lists no action maps.")
        Dim indent = last.Groups(1).Value, newline = last.Groups(2).Value
        Dim added As New StringBuilder()
        For index = 1 To count
            added.Append(indent).Append("<xmlreader processor=""ActionMap2"" filename=""actionmap/").Append(MapFileName(index)).Append(""" map=""UPDATE"" pool=""UPDATE_TEMPORARY"" />").Append(newline)
        Next
        Return Encoding.Latin1.GetBytes(text.Insert(last.Index + last.Length, added.ToString()))
    End Function

    ' The session's action map changes for the saved driving controls, or none when they're off.
    Public Shared Function Changes(context As InstallContext, controls As DrivingControls) As SessionChanges
        Dim result As New SessionChanges()
        If controls Is Nothing OrElse Not controls.Enabled OrElse controls.Bindings.Count = 0 Then Return result
        controls.Validate()
        Dim found = controls.Problems()
        If found.Count > 0 Then Throw New IOException(String.Join(Environment.NewLine, found) & Environment.NewLine & "Open Controls → Driving controls to fix these bindings.")
        result.PreferredDevices = PreferredDevices(controls)
        Dim pathsFile = PathsPath(context)
        SafeFiles.NoLinks(pathsFile)
        If Not File.Exists(pathsFile) Then Throw New IOException("DiRT 3's action map list is missing: " & pathsFile)
        Dim paths = File.ReadAllBytes(pathsFile)
        Dim loaded As New List(Of ActionMapFile)
        For Each listed In ListedMaps(paths)
            Dim mapFile = Path.Combine(context.GameRoot, listed.Replace("/"c, Path.DirectorySeparatorChar))
            If Not Path.GetFullPath(mapFile).StartsWith(Path.GetFullPath(Path.Combine(context.GameRoot, "actionmap")) & Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) Then Continue For
            SafeFiles.NoLinks(mapFile)
            If Not File.Exists(mapFile) OrElse New FileInfo(mapFile).Length > 4 * MaxMapBytes Then Continue For
            Dim map = ReadMap(listed, File.ReadAllBytes(mapFile))
            If map IsNot Nothing Then loaded.Add(map)
        Next
        Dim order = Function(name As String) If(name = "keyboard", 0, If(name = "xinput_pad", 1, 2))
        Dim groups = controls.Bindings.GroupBy(Function(b) DeviceNameOf(b), StringComparer.Ordinal).OrderBy(Function(g) order(g.Key)).ThenBy(Function(g) g.Key, StringComparer.Ordinal).ToList()
        Dim index = 0
        For Each group In groups
            If Not WritableName(group.Key) Then result.Skipped.Add(group.Key & ": its name can't be written into DiRT 3's action maps, so DiRT 3's own bindings stay.") : Continue For
            ' The loaded map DiRT 3 uses for this device: the highest priority for its name, the later one on a tie.
            Dim base = loaded.Where(Function(m) m.DeviceName = group.Key).Select(Function(m, i) (Map:=m, At:=i)).OrderBy(Function(p) p.Map.Priority).ThenBy(Function(p) p.At).LastOrDefault().Map
            If base Is Nothing AndAlso group.Key <> "keyboard" AndAlso group.Key <> "xinput_pad" Then
                base = loaded.Where(Function(m) m.DeviceName = DefaultDirectInput).OrderBy(Function(m) m.Priority).LastOrDefault()
            End If
            If base Is Nothing Then result.Skipped.Add(group.Key & ": DiRT 3's own map for it wasn't found, so DiRT 3's own bindings stay.") : Continue For
            index += 1
            result.Files.Add(MapTarget(index), Write(Compose(base, group.Key, index, group)))
            result.Devices.Add((group.Key, base.File))
        Next
        If index > 0 Then result.Files.Add(PathsTarget, ListMaps(paths, index))
        Return result
    End Function
End Class
