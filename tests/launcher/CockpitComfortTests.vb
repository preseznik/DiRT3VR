Imports DiRT3VR
Imports System.IO
Imports System.Windows.Forms

' VR cockpit comfort (#cd56379b): the head-cam edit (steady camera, driver and co-driver hidden), its place
' in a VR session's journaled seat changes, and the Controls → Seat options.
Module CockpitComfortTests
    Private Function Fixture(name As String) As Byte()
        Return File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "fixtures", "seat", name))
    End Function

    Private Function Refused(action As Action) As Boolean
        Try
            action()
        Catch ex As IOException
            Return True
        End Try
        Return False
    End Function

    Private Function Context(root As String, name As String) As InstallContext
        Dim game = Path.Combine(root, "comfort", name, "game")
        Dim documents = Path.Combine(root, "comfort", name, "documents", "hardwaresettings")
        Directory.CreateDirectory(game) : Directory.CreateDirectory(documents)
        Dim ctx As New InstallContext(game, Path.Combine(root, "comfort", name, "users"), Path.Combine(documents, "hardware_settings_config.xml"), Path.Combine(root, "comfort", name, "package"))
        Directory.CreateDirectory(Path.Combine(game, "system")) : Directory.CreateDirectory(Path.Combine(ctx.PackageRoot, "payload"))
        File.Copy(Path.Combine(AppContext.BaseDirectory, "fixtures", "hardware_settings_restrictions.xml"), Path.Combine(game, "system", "hardware_settings_restrictions.xml"), True)
        File.Copy(Path.Combine(AppContext.BaseDirectory, "fixtures", "rewrite-2026-10-31", "original.xml"), ctx.GraphicsPath, True)
        File.WriteAllText(Path.Combine(ctx.PackageRoot, "payload", "d3d11.dll"), "owned test payload")
        Directory.CreateDirectory(Path.GetDirectoryName(SeatPositions.CameraPath(ctx, "mex")))
        File.WriteAllBytes(SeatPositions.CameraPath(ctx, "mex"), Fixture("cameras-comfort.bin"))
        Directory.CreateDirectory(Path.GetDirectoryName(SeatPositions.CameraPath(ctx, "frx")))
        File.WriteAllBytes(SeatPositions.CameraPath(ctx, "frx"), Fixture("cameras.bin"))
        Return ctx
    End Function

    Public Sub Run(root As String, check As Action(Of Boolean, String))
        Dim original = Fixture("cameras-comfort.bin")
        Dim before = SeatCameras.ViewValues(original, "head-cam")
        check(before("maxHeadOffset") = "0.1350" AndAlso before("cdEnabled") = "true" AndAlso before("DisableDriver") = "false" AndAlso before.Count = 20,
              "the cockpit view's parameters are read from the view and its groups (" & before.Count & ")")

        ' Steady: DiRT 3's own head and body movement, shake, buffeting and distortion off; nothing else changes.
        Dim steady = SeatCameras.Comfort(original, False, False, True)
        Dim values = SeatCameras.ViewValues(steady, "head-cam")
        check({"maxHeadOffset", "maxBodyOffset", "shakeAngleScalingX", "minShakeAngleX", "maxShakeAngleX", "shakeAngleScalingY", "minShakeAngleY", "maxShakeAngleY"}.All(Function(name) values(name) = "0.0000") AndAlso
              values("headBuffeting") = "false" AndAlso values("cdEnabled") = "false", "a steady camera zeroes the head and body movement and the shake, and turns buffeting and distortion off")
        check(values.Where(Function(pair) Not SeatCameras.SteadyParameters.Any(Function(parameter) parameter.Name = pair.Key)).All(Function(pair) before(pair.Key) = pair.Value) AndAlso
              SeatCameras.Eye(steady).Equals(SeatCameras.Eye(original)), "the field of view, near plane, damping, driver switches and eye point stay as shipped")
        check(SeatCameras.Comfort(steady, False, False, True).SequenceEqual(steady), "applying it twice changes nothing more")

        ' The driver and co-driver, each on its own; the look-back view keeps its own switch.
        Dim driver = SeatCameras.ViewValues(SeatCameras.Comfort(original, True, False, False), "head-cam")
        Dim coDriver = SeatCameras.ViewValues(SeatCameras.Comfort(original, False, True, False), "head-cam")
        check(driver("DisableDriver") = "true" AndAlso driver("DisableCoDriver") = "false" AndAlso driver("maxHeadOffset") = "0.1350" AndAlso
              coDriver("DisableCoDriver") = "true" AndAlso coDriver("DisableDriver") = "false", "hiding the driver or the co-driver is independent of the rest")
        check(SeatCameras.ViewValues(SeatCameras.Comfort(original, True, True, True), "head-cam_reverse")("DisableDriver") = "false", "only the cockpit view changes")

        ' A car without these parameters keeps them absent; a parameter of another type is refused.
        Dim plain = Fixture("cameras.bin")
        check(SeatCameras.Comfort(plain, True, True, True).SequenceEqual(plain), "a camera file without these parameters is left exactly as it is")
        Dim odd = CType(original.Clone(), Byte())
        Dim at = Text.Encoding.ASCII.GetString(odd).IndexOf("bool" & ChrW(0), StringComparison.Ordinal)
        odd(at) = CByte(AscW("x"c))
        check(Refused(Sub() SeatCameras.Comfort(odd, False, False, True)), "a switch that isn't a bool is refused (the car keeps its own camera)")

        ' The session: every installed car's cockpit view, after any seat offset, in the journal; VR only.
        Dim settings As New LauncherSettings With {.SteamRuntimeManifest = "", .VdxrRuntimeManifest = "", .CockpitHideCoDriver = True,
            .SeatOffsets = New Dictionary(Of String, SeatOffset) From {{"mex", New SeatOffset With {.Up = 2, .Steady = True}}}}
        settings.Validate()
        check(settings.CockpitSteady AndAlso Not settings.CockpitHideDriver AndAlso Not settings.SeatOffsets("mex").Steady, "a steady camera is on by default; stored positions carry no session flags")
        Dim effective = SeatPositions.Effective(settings)
        check(effective.Count = EventCatalog.Current.Cars.Count AndAlso effective.Values.All(Function(item) item.Steady AndAlso item.HideCoDriver AndAlso Not item.HideDriver AndAlso Not item.Cockpit) AndAlso
              effective("mex").Up = 2 AndAlso effective("frx").IsZero AndAlso Not effective("frx").IsUnchanged, "every car gets the comfort choices; mex also keeps its seat position")
        check(SeatPositions.Effective(settings, vr:=False).Count = 0, "the monitor never gets them (and gets seat positions only when asked)")
        settings.SeatOnDesktop = True
        check(SeatPositions.Effective(settings, vr:=False).Keys.SequenceEqual({"mex"}) AndAlso Not SeatPositions.Effective(settings, vr:=False)("mex").VrOnly, "with seat positions on the monitor, still no comfort changes there")
        check(SeatPositions.Effective(New LauncherSettings With {.CockpitSteady = False}).Count = 0, "with every option off a VR session leaves the camera files alone")
        Dim mexEdit = SeatPositions.Apply(original, effective("mex"))
        check(mexEdit.SequenceEqual(SeatCameras.Comfort(SeatCameras.Offset(original, 0, 0.02, 0), False, True, True)) AndAlso SeatCameras.Eye(mexEdit).Y = SeatCameras.Eye(original).Y + 0.02,
              "the seat offset and the comfort edit go into one camera file")

        Dim ctx = Context(root, "session")
        Dim seats = SeatPositions.Changes(ctx, settings)
        check(seats.Files.Keys.OrderBy(Function(key) key, StringComparer.Ordinal).SequenceEqual({"seat/frx", "seat/mex"}) AndAlso seats.Skipped.Count = 0 AndAlso
              seats.Files("seat/frx").SequenceEqual(Fixture("cameras.bin")), "the installed cars' camera files are in the session (frx has none of the parameters)")
        Dim changes = ExperimentalFiles.Changes(ctx, 1800, 1950).ToDictionary(Function(pair) pair.Key, Function(pair) pair.Value)
        For Each pair In seats.Files
            changes.Add(pair.Key, pair.Value)
        Next
        Dim transaction As New OwnedFileTransaction(ctx)
        transaction.Prepare(changes, experimental:=True, apply:=False, seats:=seats.Offsets)
        Dim journal = SafeFiles.ReadJson(Of RecoveryJournal)(transaction.JournalPath)
        check(journal.Seats("mex").Steady AndAlso journal.Seats("mex").HideCoDriver AndAlso journal.Seats("mex").Up = 2 AndAlso journal.Seats("frx").Steady, "the journal records the comfort choices with each car's offset")
        For Each pair In changes
            Dim target = If(SeatPositions.IsTarget(pair.Key), SeatPositions.CameraPath(ctx, pair.Key.Substring(5)), If(pair.Key = "proxy", Path.Combine(ctx.GameRoot, "d3d11.dll"), If(pair.Key = "graphics", ctx.GraphicsPath, Path.Combine(ctx.GameRoot, "system", "hardware_settings_restrictions.xml"))))
            File.WriteAllBytes(target, pair.Value)
        Next
        check(SeatCameras.ViewValues(File.ReadAllBytes(SeatPositions.CameraPath(ctx, "mex")), "head-cam")("cdEnabled") = "false", "during the session the cockpit view is steady")
        check(transaction.Recover() AndAlso File.ReadAllBytes(SeatPositions.CameraPath(ctx, "mex")).SequenceEqual(original) AndAlso File.ReadAllBytes(SeatPositions.CameraPath(ctx, "frx")).SequenceEqual(Fixture("cameras.bin")),
              "after the session every camera file is back as shipped")

        ' A desktop journal can't carry them.
        Dim desktop As New OwnedFileTransaction(Context(root, "desktop"))
        check(Refused(Sub() desktop.Prepare(New Dictionary(Of String, Byte()) From {{"seat/mex", SeatPositions.Apply(original, New SeatOffset With {.Up = 3, .Steady = True})}}, apply:=False,
                                            seats:=New Dictionary(Of String, SeatOffset) From {{"mex", New SeatOffset With {.Up = 3, .Steady = True}}})) AndAlso Not desktop.Pending,
              "cockpit comfort belongs to a VR session")

        ' Settings profiles keep this PC's choices, like the other seat settings.
        Dim local As New LauncherSettings With {.CockpitSteady = False, .CockpitHideDriver = True, .DesktopFov = 12}
        Dim applied = ProfileValues.Capture(New LauncherSettings With {.SteamRuntimeManifest = "", .VdxrRuntimeManifest = ""}).ApplyTo(local)
        check(Not applied.CockpitSteady AndAlso applied.CockpitHideDriver AndAlso Not applied.CockpitHideCoDriver AndAlso applied.DesktopFov = 12, "a settings profile keeps this PC's cockpit choices")

        RunFieldOfView(root, check)
    End Sub

    ' The cockpit field of view on the monitor (#b330cc4f): each car's own plus a change, for Launch only.
    Private Sub RunFieldOfView(root As String, check As Action(Of Boolean, String))
        Dim original = Fixture("cameras-comfort.bin")
        Dim wider = SeatCameras.FieldOfView(original, 10)
        Dim values = SeatCameras.ViewValues(wider, "head-cam"), before = SeatCameras.ViewValues(original, "head-cam")
        check(values("fov") = "60.0000" AndAlso SeatCameras.ViewValues(SeatCameras.FieldOfView(original, -20), "head-cam")("fov") = "30.0000" AndAlso
              values.Where(Function(pair) pair.Key <> "fov").All(Function(pair) before(pair.Key) = pair.Value), "the cockpit's own 50° becomes 60° or 30°; nothing else changes")
        check(Refused(Sub() SeatCameras.FieldOfView(original, 31)) AndAlso Refused(Sub() SeatCameras.FieldOfView(original, -21)) AndAlso Refused(Sub() SeatCameras.FieldOfView(Fixture("cameras.bin"), 5)),
              "changes beyond 30° wider or 20° narrower, and a field of view that isn't a scalar, are refused")
        check(Refused(Sub() Call New LauncherSettings With {.DesktopFov = 31}.Validate()) AndAlso Refused(Sub() Call New LauncherSettings With {.DesktopFov = -21}.Validate()) AndAlso
              Not Refused(Sub() Call New LauncherSettings With {.DesktopFov = -20}.Validate()), "the setting stays within 20° narrower and 30° wider")

        ' Launch only, every installed car, with or without seat positions on the monitor.
        Dim settings As New LauncherSettings With {.SteamRuntimeManifest = "", .VdxrRuntimeManifest = "", .DesktopFov = 10,
            .SeatOffsets = New Dictionary(Of String, SeatOffset) From {{"mex", New SeatOffset With {.Up = 2}}}}
        settings.Validate()
        Dim desktop = SeatPositions.Effective(settings, vr:=False)
        check(desktop.Count = EventCatalog.Current.Cars.Count AndAlso desktop.Values.All(Function(item) item.Fov = 10 AndAlso item.IsZero AndAlso Not item.VrOnly), "on the monitor every car gets the field of view, without seat positions unless asked")
        check(SeatPositions.Effective(settings).Values.All(Function(item) item.Fov = 0), "VR never gets it")
        check(SeatPositions.OnDesktop(settings) AndAlso DesktopStartupSession.NeedsSession(settings) AndAlso Not SeatPositions.OnDesktop(New LauncherSettings), "a changed field of view makes Launch a managed session")
        settings.SeatOnDesktop = True
        check(SeatPositions.Effective(settings, vr:=False)("mex").Up = 2 AndAlso SeatPositions.Effective(settings, vr:=False)("mex").Fov = 10, "with seat positions on the monitor, mex gets both")
        check(SeatPositions.Apply(original, SeatPositions.Effective(settings, vr:=False)("mex")).SequenceEqual(SeatCameras.FieldOfView(SeatCameras.Offset(original, 0, 0.02, 0), 10)), "both in one camera file")

        Dim ctx = Context(root, "fov")
        Dim seats = SeatPositions.Changes(ctx, settings, vr:=False)
        check(seats.Files.Keys.SequenceEqual({"seat/mex"}) AndAlso seats.Skipped.SequenceEqual({EventCatalog.Current.FindCar("frx").Name}), "a car whose camera file has no usable field of view keeps its own and is named")
        Dim transaction As New OwnedFileTransaction(ctx)
        transaction.Prepare(DesktopStartupSession.Changes(ctx, settings, Nothing, seats.Files), apply:=False, seats:=seats.Offsets)
        Dim journal = SafeFiles.ReadJson(Of RecoveryJournal)(transaction.JournalPath)
        check(journal.Schema = 9 AndAlso journal.Seats("mex").Fov = 10 AndAlso journal.Seats("mex").Up = 2, "a desktop journal (schema 9) records the field of view with the seat")
        File.WriteAllBytes(SeatPositions.CameraPath(ctx, "mex"), seats.Files("seat/mex"))
        check(transaction.Recover() AndAlso File.ReadAllBytes(SeatPositions.CameraPath(ctx, "mex")).SequenceEqual(original), "after the session the camera file is back as shipped")
        Dim vr As New OwnedFileTransaction(Context(root, "fov-vr"))
        check(Refused(Sub() vr.Prepare(New Dictionary(Of String, Byte()) From {{"seat/mex", SeatPositions.Apply(original, New SeatOffset With {.Fov = 5})}}, experimental:=True, apply:=False,
                                       seats:=New Dictionary(Of String, SeatOffset) From {{"mex", New SeatOffset With {.Fov = 5}}})) AndAlso Not vr.Pending, "the monitor's field of view belongs to a desktop session")
    End Sub

    Public Sub RunUi(root As String, check As Action(Of Boolean, String))
        Dim game = Path.Combine(root, "comfort-ui")
        Directory.CreateDirectory(game)
        Dim ctx As New InstallContext(game, Path.Combine(root, "comfort-ui-users"), Path.Combine(game, "graphics.xml"), root)
        Call New LauncherSettings With {.SteamRuntimeManifest = "", .VdxrRuntimeManifest = ""}.Save(ctx)
        Using form As New MainForm(ctx)
            form.ShowInTaskbar = False : form.StartPosition = FormStartPosition.Manual : form.Location = New Drawing.Point(-30000, -30000)
            form.Show() : Application.DoEvents()
            Dim box = Function(name As String) DirectCast(form.Controls.Find(name, True).Single(), CheckBox)
            check(box("SeatSteadyCamera").Checked AndAlso Not box("SeatHideDriver").Checked AndAlso Not box("SeatHideCoDriver").Checked AndAlso Not form.HasUnsavedSettings(),
                  "Controls → Seat: a steady camera on, the driver and co-driver shown, by default")
            box("SeatSteadyCamera").Checked = False : box("SeatHideDriver").Checked = True : Application.DoEvents()
            check(Not form.CurrentSettings().CockpitSteady AndAlso form.CurrentSettings().CockpitHideDriver AndAlso form.HasUnsavedSettings(), "each option is a launcher setting")
            DirectCast(form.Controls.Find("SaveSettings", True).Single(), Button).PerformClick()
            Dim saved = LauncherSettings.Load(ctx)
            check(Not saved.CockpitSteady AndAlso saved.CockpitHideDriver AndAlso Not saved.CockpitHideCoDriver AndAlso Not form.HasUnsavedSettings(), "Save settings keeps them")
            Dim fov = DirectCast(form.Controls.Find("SeatDesktopFov", True).Single(), ValueSlider)
            check(fov.Value = 0 AndAlso form.CurrentSettings().DesktopFov = 0, "the monitor's field of view is DiRT 3's own by default")
            fov.Value = 10 : Application.DoEvents()
            check(form.CurrentSettings().DesktopFov = 10 AndAlso form.HasUnsavedSettings(), "10° wider is a launcher setting")
            form.Close()
        End Using
        Using form As New MainForm(ctx)
            form.ShowInTaskbar = False : form.StartPosition = FormStartPosition.Manual : form.Location = New Drawing.Point(-30000, -30000)
            form.Show() : Application.DoEvents()
            check(Not DirectCast(form.Controls.Find("SeatSteadyCamera", True).Single(), CheckBox).Checked AndAlso DirectCast(form.Controls.Find("SeatHideDriver", True).Single(), CheckBox).Checked,
                  "the saved choices show again")
            form.Close()
        End Using
    End Sub
End Module
