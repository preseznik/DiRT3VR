Imports DiRT3VR
Imports System.IO
Imports System.Text

' Launcher driving bindings in a direct practice session: complete per-device action maps and the
' journaled actionMapPaths.xml (Wrench's runs 93–94). Synthetic stock maps; real-file parity is
' checked on Windows.
Module SessionActionMapsTests
    Private Const WheelId As String = "{01234567-0000-0000-0000-504944564944}", WheelProduct As String = "{C262046D-0000-0000-0000-504944564944}"
    Private Const OtherId As String = "{01234569-0000-0000-0000-504944564944}", OtherProduct As String = "{C29B046D-0000-0000-0000-504944564944}"

    Private Const Paths As String =
        "<?xml version=""1.0"" encoding=""utf-8""?>" & vbCrLf &
        "<xmlreaders>" & vbCrLf &
        "    <xmlreader processor=""ActionMap2"" filename=""actionmap/dinput_default.xml"" map=""UPDATE"" pool=""UPDATE_TEMPORARY"" />" & vbCrLf &
        "    <xmlreader processor=""ActionMap2"" filename=""actionmap/wm_keyboard.xml"" map=""UPDATE"" pool=""UPDATE_TEMPORARY"" />" & vbCrLf &
        "    <xmlreader processor=""ActionMap2"" filename=""actionmap/xinput_pad.xml"" map=""UPDATE"" pool=""UPDATE_TEMPORARY"" />" & vbCrLf &
        "    <xmlreader processor=""ActionMap2"" filename=""actionmap/test_wheel.xml"" map=""UPDATE"" pool=""UPDATE_TEMPORARY"" />" & vbCrLf &
        "    <!-- <xmlreader processor=""ActionMap2"" filename=""actionmap/commented.xml"" map=""UPDATE"" pool=""UPDATE_TEMPORARY"" /> -->" & vbCrLf &
        "    <xmlreader processor=""Other"" filename=""actionmap/not_a_map.xml"" />" & vbCrLf &
        "</xmlreaders>" & vbCrLf

    Private Function Axis(name As String, Optional type As String = "uniDirectionalPositive", Optional extra As String = "") As String
        Return "    <Axis axisName=""" & name & """>" & vbCrLf & "      <CalibratorSimple type=""" & type & """" & extra & " />" & vbCrLf & "    </Axis>" & vbCrLf
    End Function

    Private Function Map(name As String, device As String, priority As Integer, ParamArray actions As String()) As String
        Dim text As New StringBuilder("<!-- deadZone and saturation default to 0.2 / 0.8 -->" & vbCrLf & "<ActionMap actionMapName=""" & name & """ deviceName=""" & device & """ priority=""" & priority & """>" & vbCrLf)
        For Each action In actions
            text.Append(action)
        Next
        Return text.Append("</ActionMap>" & vbCrLf).ToString()
    End Function

    Private Function Act(name As String, ParamArray axes As String()) As String
        Return "  <Action actionName=""" & name & """>" & vbCrLf & String.Concat(axes) & "  </Action>" & vbCrLf
    End Function

    Private Function Context(root As String, name As String) As InstallContext
        Dim game = Path.Combine(root, "actionmaps", name, "game")
        Dim documents = Path.Combine(root, "actionmaps", name, "documents", "hardwaresettings")
        Directory.CreateDirectory(Path.Combine(game, "actionmap")) : Directory.CreateDirectory(documents) : Directory.CreateDirectory(Path.Combine(game, "system"))
        Dim ctx As New InstallContext(game, Path.Combine(root, "actionmaps", name, "users"), Path.Combine(documents, "hardware_settings_config.xml"), Path.Combine(root, "actionmaps", name, "package"))
        Directory.CreateDirectory(Path.Combine(ctx.PackageRoot, "payload"))
        File.WriteAllText(Path.Combine(ctx.PackageRoot, "payload", "d3d11.dll"), "owned test payload")
        File.Copy(Path.Combine(AppContext.BaseDirectory, "fixtures", "hardware_settings_restrictions.xml"), Path.Combine(game, "system", "hardware_settings_restrictions.xml"), True)
        File.Copy(Path.Combine(AppContext.BaseDirectory, "fixtures", "rewrite-2026-10-31", "original.xml"), ctx.GraphicsPath, True)
        Dim folder = Path.Combine(game, "actionmap")
        File.WriteAllText(Path.Combine(folder, "actionMapPaths.xml"), Paths)
        File.WriteAllText(Path.Combine(folder, "wm_keyboard.xml"), Map("wm_keyboard", "keyboard", 0,
            Act("Accelerate", Axis("vk_code_0x26"), Axis("vk_code_0x68")), Act("Pause", Axis("vk_code_0x1B")), Act("Menu Back", Axis("vk_code_0x1B"))))
        File.WriteAllText(Path.Combine(folder, "xinput_pad.xml"), Map("xinput_pad", "xinput_pad", 0,
            Act("Accelerate", Axis("xinput_right_trigger", extra:=" deadZone=""0.0"" saturation=""1.0""")),
            Act("Steer Left", Axis("xinput_left_stick_x", "biDirectionalLower", " deadZone=""0.2"" saturation=""1.0"""), Axis("xinput_dpad_left")),
            Act("Pause", Axis("xinput_button_start"))))
        File.WriteAllText(Path.Combine(folder, "dinput_default.xml"), Map("diDeviceDefault", "di_device_default", 16,
            Act("Accelerate", Axis("di_y_axis_rotation", "uniDirectionalNegative", " deadZone=""0.0"" saturation=""1.0""")),
            Act("Gear Up", Axis("di_button_2")), Act("Pause", Axis("di_button_9"))))
        ' Lenient, as DiRT 3: a commented-out action is not a binding, and the map isn't closed.
        File.WriteAllText(Path.Combine(folder, "test_wheel.xml"), Map("test_wheel", "Test wheel", 8,
            Act("Accelerate", Axis("di_y_axis", "uniDirectionalNegative", " deadZone=""0.0"" saturation=""1.0""")),
            "<!--" & Act("Horn", Axis("di_button_20")) & "-->" & vbCrLf,
            Act("Brake", Axis("di_z_axis", "uniDirectionalNegative"))).Replace("</ActionMap>" & vbCrLf, ""))
        ' Not listed in actionMapPaths.xml, so DiRT 3 never loads it, whatever its priority.
        File.WriteAllText(Path.Combine(folder, "unlisted_wheel.xml"), Map("unlisted", "Test wheel", 50, Act("Brake", Axis("di_button_1"))))
        Return ctx
    End Function

    Private Function Refused(run As Action) As Boolean
        Try
            run()
        Catch ex As IOException
            Return True
        End Try
        Return False
    End Function

    Private Function Bound(action As String, device As String, id As String, type As String, input As String, Optional calibration As String = "uniDirectionalPositive") As DrivingBinding
        Return New DrivingBinding With {.Action = action, .Device = device, .DeviceId = id, .DeviceType = type, .Input = input, .Calibration = calibration}
    End Function

    Public Sub Run(root As String, check As Action(Of Boolean, String))
        Dim ctx = Context(root, "maps")
        Dim pathsBytes = File.ReadAllBytes(SessionActionMaps.PathsPath(ctx))
        check(SessionActionMaps.ListedMaps(pathsBytes).SequenceEqual({"actionmap/dinput_default.xml", "actionmap/wm_keyboard.xml", "actionmap/xinput_pad.xml", "actionmap/test_wheel.xml"}),
              "only the ActionMap2 entries outside comments are DiRT 3's loaded maps")
        Dim wheelMap = SessionActionMaps.ReadMap("actionmap/test_wheel.xml", File.ReadAllBytes(Path.Combine(ctx.GameRoot, "actionmap", "test_wheel.xml")))
        check(wheelMap.DeviceName = "Test wheel" AndAlso wheelMap.Priority = 8 AndAlso wheelMap.Actions.Select(Function(a) a.Name).SequenceEqual({"Accelerate", "Brake"}) AndAlso
              wheelMap.Actions(0).Axes(0).DeadZone = "0.0" AndAlso wheelMap.Actions(1).Axes(0).DeadZone Is Nothing, "a stock map is read leniently, without commented-out actions")

        ' Off, or no bindings: nothing changes.
        check(SessionActionMaps.Changes(ctx, New DrivingControls()).Files.Count = 0 AndAlso
              SessionActionMaps.Changes(ctx, New DrivingControls With {.Bindings = New List(Of DrivingBinding) From {Bound("Brake", "Test wheel", WheelId, WheelProduct, "di_z_axis")}}).Files.Count = 0,
              "driving controls that are off or empty change nothing")

        Dim controls As New DrivingControls With {.Enabled = True, .Bindings = New List(Of DrivingBinding) From {
            New DrivingBinding With {.Action = "Accelerate", .Device = "Keyboard", .DeviceId = "keyboard", .DeviceType = "keyboard", .Input = "vk_code_0x57"},
            Bound("Steer Left", "Xbox controller 1", "xinput:0", "xinput_pad", "xinput_left_stick_x", "biDirectionalLower"),
            Bound("Steer Right", "Xbox controller 1", "xinput:0", "xinput_pad", "xinput_left_stick_x", "biDirectionalUpper"),
            Bound("Brake", "Test wheel", WheelId, WheelProduct, "di_z_axis_rotation", "uniDirectionalNegative"),
            Bound("Gear 1", "Test wheel", WheelId, WheelProduct, "di_button_12"),
            Bound("Gear Up", "Other wheel", OtherId, OtherProduct, "di_button_4"),
            Bound("Pause", "Bad & wheel", "{01234570-0000-0000-0000-504944564944}", "{C29C046D-0000-0000-0000-504944564944}", "di_button_7")}}
        controls.Bindings(3).DeadZone = 0.05D : controls.Bindings(3).Saturation = 0.9D
        Dim session = SessionActionMaps.Changes(ctx, controls)
        check(session.Files.Keys.Order().SequenceEqual({"actionmap/1", "actionmap/2", "actionmap/3", "actionmap/4", "actionpaths"}) AndAlso
              session.Devices.Select(Function(d) d.DeviceName & "=" & d.BaseFile).SequenceEqual({"keyboard=actionmap/wm_keyboard.xml", "xinput_pad=actionmap/xinput_pad.xml", "Other wheel=actionmap/dinput_default.xml", "Test wheel=actionmap/test_wheel.xml"}),
              "one map per bound device: keyboard, Xbox, then wheels by name; a wheel without its own map builds on dinput_default (" & String.Join(", ", session.Devices) & ")")
        check(session.Skipped.Count = 1 AndAlso session.Skipped(0).StartsWith("Bad & wheel:", StringComparison.Ordinal) AndAlso session.Note.StartsWith("Launcher driving bindings not applied for Bad & wheel", StringComparison.Ordinal),
              "a device name that can't be written into an action map is skipped and named")

        Dim keyboardMap = Encoding.ASCII.GetString(session.Files("actionmap/1"))
        check(keyboardMap.Contains("<ActionMap actionMapName=""dirt3vrControls1"" deviceName=""keyboard"" priority=""100"">", StringComparison.Ordinal), "the keyboard map has DiRT 3's keyboard name and priority 100 (" & keyboardMap.Split(ChrW(10))(1) & ")")
        check(keyboardMap.Contains("  <Action actionName=""Accelerate"">" & vbCrLf & "    <Axis axisName=""vk_code_0x57"">" & vbCrLf & "      <CalibratorSimple type=""uniDirectionalPositive"" />" & vbCrLf & "    </Axis>" & vbCrLf & "  </Action>", StringComparison.Ordinal) AndAlso
              Not keyboardMap.Contains("vk_code_0x26", StringComparison.Ordinal) AndAlso Not keyboardMap.Contains("vk_code_0x68", StringComparison.Ordinal), "a bound action's stock keys are replaced, a key has no dead zone or saturation")
        check(keyboardMap.Contains("<Action actionName=""Pause"">" & vbCrLf & "    <Axis axisName=""vk_code_0x1B"">", StringComparison.Ordinal) AndAlso keyboardMap.Contains("Menu Back", StringComparison.Ordinal) AndAlso
              keyboardMap.IndexOf("Accelerate", StringComparison.Ordinal) < keyboardMap.IndexOf("""Pause""", StringComparison.Ordinal), "unbound actions keep DiRT 3's keys, in the stock order")
        Dim padMap = Encoding.ASCII.GetString(session.Files("actionmap/2"))
        check(padMap.Contains("<Axis axisName=""xinput_left_stick_x"">" & vbCrLf & "      <CalibratorSimple type=""biDirectionalLower"" deadZone=""0.0"" saturation=""1.0"" />", StringComparison.Ordinal) AndAlso
              Not padMap.Contains("xinput_dpad_left", StringComparison.Ordinal) AndAlso padMap.Contains("""Steer Right""", StringComparison.Ordinal) AndAlso
              padMap.Contains("<CalibratorSimple type=""uniDirectionalPositive"" deadZone=""0.0"" saturation=""1.0"" />", StringComparison.Ordinal), "an axis writes its dead zone and saturation; the stock trigger stays as it was")
        Dim otherMap = Encoding.ASCII.GetString(session.Files("actionmap/3"))
        check(otherMap.Contains("deviceName=""Other wheel""", StringComparison.Ordinal) AndAlso otherMap.Contains("di_y_axis_rotation", StringComparison.Ordinal) AndAlso
              otherMap.Contains("<Action actionName=""Gear Up"">" & vbCrLf & "    <Axis axisName=""di_button_4"">", StringComparison.Ordinal) AndAlso Not otherMap.Contains("di_button_2", StringComparison.Ordinal),
              "a wheel without its own map keeps dinput_default's other bindings")
        Dim wheelText = Encoding.ASCII.GetString(session.Files("actionmap/4"))
        check(wheelText.Contains("<CalibratorSimple type=""uniDirectionalNegative"" deadZone=""0.05"" saturation=""0.9"" />", StringComparison.Ordinal) AndAlso
              wheelText.IndexOf("""Accelerate""", StringComparison.Ordinal) < wheelText.IndexOf("""Brake""", StringComparison.Ordinal) AndAlso wheelText.IndexOf("""Brake""", StringComparison.Ordinal) < wheelText.IndexOf("""Gear 1""", StringComparison.Ordinal) AndAlso
              Not wheelText.Contains("Horn", StringComparison.Ordinal) AndAlso Not wheelText.Contains("di_button_1""", StringComparison.Ordinal) AndAlso wheelText.EndsWith("</ActionMap>" & vbCrLf, StringComparison.Ordinal),
              "the wheel's own listed map is the base, calibration is written, a new action goes last, and the map is closed")

        For index = 1 To 4
            Dim value = index
            check(Not Refused(Sub() SessionActionMaps.Validate(session.Files(SessionActionMaps.MapTarget(value)), value)), "session map " & index & " passes recovery's check")
        Next
        check(Refused(Sub() SessionActionMaps.Validate(session.Files("actionmap/1"), 2)), "a map under another number is refused")
        For Each tampered In {keyboardMap.Replace("priority=""100""", "priority=""99"""), keyboardMap.Replace("  </Action>", "  </Action>" & vbCrLf & "<!-- extra -->", StringComparison.Ordinal),
                              keyboardMap.Replace("vk_code_0x57", "vk_code_0x57"" bogus=""1"), keyboardMap.Replace(vbCrLf, vbLf), keyboardMap & "trailing", keyboardMap.Replace("deviceName=""keyboard""", "deviceName=""key&amp;board""")}
            Dim value = tampered
            check(Refused(Sub() SessionActionMaps.Validate(Encoding.ASCII.GetBytes(value), 1)), "a changed session map is refused")
        Next

        Dim listed = Encoding.Latin1.GetString(session.Files("actionpaths"))
        Dim wheelLine = "    <xmlreader processor=""ActionMap2"" filename=""actionmap/test_wheel.xml"" map=""UPDATE"" pool=""UPDATE_TEMPORARY"" />" & vbCrLf
        Dim added = String.Concat(Enumerable.Range(1, 4).Select(Function(i) "    <xmlreader processor=""ActionMap2"" filename=""actionmap/dirt3vr-controls-" & i & ".xml"" map=""UPDATE"" pool=""UPDATE_TEMPORARY"" />" & vbCrLf))
        check(listed = Paths.Replace(wheelLine, wheelLine & added), "actionMapPaths.xml lists the session maps right after the last stock map, nothing else changes")
        check(SessionActionMaps.ListMaps(Encoding.ASCII.GetBytes(Paths.Replace(vbCrLf, vbLf)), 1).SequenceEqual(Encoding.ASCII.GetBytes(Paths.Replace(vbCrLf, vbLf).Replace(wheelLine.Replace(vbCrLf, vbLf), wheelLine.Replace(vbCrLf, vbLf) & "    <xmlreader processor=""ActionMap2"" filename=""actionmap/dirt3vr-controls-1.xml"" map=""UPDATE"" pool=""UPDATE_TEMPORARY"" />" & vbLf))),
              "the list keeps its own line endings")
        check(Refused(Sub() SessionActionMaps.ListMaps(session.Files("actionpaths"), 1)) AndAlso Refused(Sub() SessionActionMaps.ListMaps(Encoding.ASCII.GetBytes("<xmlreaders />"), 1)) AndAlso
              Refused(Sub() SessionActionMaps.ListMaps(pathsBytes, 0)) AndAlso Refused(Sub() SessionActionMaps.ListMaps(pathsBytes, 17)), "a list that already has DiRT3VR's maps, has none, or a wrong count is refused")

        Dim conflict As New DrivingControls With {.Enabled = True, .Bindings = New List(Of DrivingBinding) From {controls.Bindings(1), Changed(controls.Bindings(1), "Steer Right")}}
        check(Refused(Sub() SessionActionMaps.Changes(ctx, conflict)), "bindings with problems stop the launch with the reason")
        File.Delete(SessionActionMaps.PathsPath(ctx))
        check(Refused(Sub() SessionActionMaps.Changes(ctx, controls)), "a missing action map list stops the launch")
        File.WriteAllBytes(SessionActionMaps.PathsPath(ctx), pathsBytes)

        RunRecovery(root, check, controls)
    End Sub

    Private Function Changed(binding As DrivingBinding, action As String) As DrivingBinding
        Dim result = binding.Copy()
        result.Action = action
        Return result
    End Function

    Private Sub RunRecovery(root As String, check As Action(Of Boolean, String), controls As DrivingControls)
        Dim settings As New LauncherSettings With {.SteamRuntimeManifest = "", .VdxrRuntimeManifest = "", .LaunchMode = "direct",
            .DirectRoute = EventCatalog.Current.Routes.Single(Function(route) route.Track = "aspen" AndAlso route.Route = "route_0" AndAlso route.Discipline = "rallycross").Id, .DirectCar = "frx"}
        settings.Validate()

        ' Desktop direct practice: config, renderer, the session maps and the list (schema 8).
        Dim ctx = Context(root, "desktop")
        controls.Bindings.RemoveAll(Function(b) b.Device = "Bad & wheel")
        controls.Save(ctx)
        Dim originalPaths = File.ReadAllBytes(SessionActionMaps.PathsPath(ctx))
        Dim changes = DesktopStartupSession.Changes(ctx, settings)
        check(changes.Keys.Order().SequenceEqual({"actionmap/1", "actionmap/2", "actionmap/3", "actionmap/4", "actionpaths", "direct", "proxy"}), "desktop direct practice adds the session maps to its changes")
        Dim transaction As New OwnedFileTransaction(ctx)
        transaction.Prepare(changes, apply:=False)
        Dim journal = SafeFiles.ReadJson(Of RecoveryJournal)(transaction.JournalPath)
        check(journal.Schema = 8 AndAlso journal.ActionMaps.GetValueOrDefault() = 4 AndAlso Not File.Exists(SessionActionMaps.MapPath(ctx, 1)), "the session maps make a schema 8 journal (older launchers refuse it) before anything is deployed")
        Dim deploy = Sub()
                         File.WriteAllBytes(DirectPractice.ConfigPath(ctx), changes("direct"))
                         File.WriteAllBytes(Path.Combine(ctx.GameRoot, "d3d11.dll"), changes("proxy"))
                         File.WriteAllBytes(SessionActionMaps.PathsPath(ctx), changes("actionpaths"))
                         For index = 1 To 4
                             File.WriteAllBytes(SessionActionMaps.MapPath(ctx, index), changes(SessionActionMaps.MapTarget(index)))
                         Next
                     End Sub
        deploy()
        Dim journalText = File.ReadAllText(transaction.JournalPath)
        For Each broken In {journalText.Replace("""ActionMaps"": 4", """ActionMaps"": 3"), System.Text.RegularExpressions.Regex.Replace(journalText, """Schema"":\s*8", """Schema"": 5")}
            File.WriteAllText(transaction.JournalPath, broken)
            check(Refused(Sub() transaction.Recover()) AndAlso File.Exists(SessionActionMaps.MapPath(ctx, 1)), "a journal with the wrong map count or schema is refused and the files preserved")
        Next
        File.WriteAllText(transaction.JournalPath, journalText)
        check(transaction.Recover() AndAlso Not Enumerable.Range(1, 4).Any(Function(i) File.Exists(SessionActionMaps.MapPath(ctx, i))) AndAlso
              File.ReadAllBytes(SessionActionMaps.PathsPath(ctx)).SequenceEqual(originalPaths) AndAlso Not File.Exists(DirectPractice.ConfigPath(ctx)), "recovery removes the session maps and restores actionMapPaths.xml")

        ' A session map someone else left there is preserved, and the launch refused.
        File.WriteAllText(SessionActionMaps.MapPath(ctx, 2), "someone else's map")
        check(Refused(Sub() transaction.Prepare(DesktopStartupSession.Changes(ctx, settings), apply:=False)) AndAlso Not transaction.Pending AndAlso File.ReadAllText(SessionActionMaps.MapPath(ctx, 2)) = "someone else's map",
              "an existing dirt3vr-controls file is preserved and the launch refused")
        File.Delete(SessionActionMaps.MapPath(ctx, 2))

        ' Only the approved transforms are staged.
        Dim tamperedList = New Dictionary(Of String, Byte())(changes)
        tamperedList("actionpaths") = SessionActionMaps.ListMaps(originalPaths, 3)
        check(Refused(Sub() transaction.Prepare(tamperedList, apply:=False)) AndAlso Not transaction.Pending, "a list naming a different number of maps is refused")
        Dim tamperedMap = New Dictionary(Of String, Byte())(changes)
        tamperedMap("actionmap/1") = Encoding.ASCII.GetBytes(Encoding.ASCII.GetString(changes("actionmap/1")).Replace("vk_code_0x57", "vk_code_0x58"))
        check(Not Refused(Sub() SessionActionMaps.Validate(tamperedMap("actionmap/1"), 1)), "a well-formed map with another key is still DiRT3VR's format")
        tamperedMap("actionmap/1") = Encoding.ASCII.GetBytes(Encoding.ASCII.GetString(changes("actionmap/1")) & "<!-- x -->")
        check(Refused(Sub() transaction.Prepare(tamperedMap, apply:=False)) AndAlso Not transaction.Pending, "a map that isn't DiRT3VR's format is refused")
        Dim gap = New Dictionary(Of String, Byte())(changes)
        gap.Remove("actionmap/2")
        check(Refused(Sub() transaction.Prepare(gap, apply:=False)) AndAlso Not transaction.Pending, "session maps must be numbered without gaps")
        Dim noDirect = changes.Where(Function(pair) pair.Key.StartsWith("action", StringComparison.Ordinal)).ToDictionary(Function(pair) pair.Key, Function(pair) pair.Value)
        check(Refused(Sub() transaction.Prepare(noDirect, apply:=False)) AndAlso Not transaction.Pending, "session maps belong to direct practice")

        ' Launch VR: with the VR session's targets (schema 8 too).
        Dim vrChanges = ExperimentalFiles.Changes(ctx, 1800, 1950).ToDictionary(Function(pair) pair.Key, Function(pair) pair.Value)
        vrChanges.Add("direct", DirectPractice.Config(settings))
        For Each pair In SessionActionMaps.Changes(ctx, DrivingControls.Load(ctx)).Files
            vrChanges.Add(pair.Key, pair.Value)
        Next
        transaction.Prepare(vrChanges, experimental:=True, apply:=False)
        check(SafeFiles.ReadJson(Of RecoveryJournal)(transaction.JournalPath).Schema = 8, "a VR direct practice session with driving bindings writes a schema 8 journal")
        File.WriteAllBytes(SessionActionMaps.MapPath(ctx, 1), vrChanges("actionmap/1"))
        File.WriteAllBytes(SessionActionMaps.PathsPath(ctx), vrChanges("actionpaths"))
        check(transaction.Recover() AndAlso Not File.Exists(SessionActionMaps.MapPath(ctx, 1)) AndAlso File.ReadAllBytes(SessionActionMaps.PathsPath(ctx)).SequenceEqual(originalPaths), "VR recovery removes the maps and restores the list")

        ' A normal launch never writes session maps.
        Dim normal As New LauncherSettings With {.SteamRuntimeManifest = "", .VdxrRuntimeManifest = "", .SkipStartupMovies = False}
        check(DesktopStartupSession.Changes(ctx, normal).Count = 0, "a normal launch keeps DiRT 3's own maps")
    End Sub
End Module
