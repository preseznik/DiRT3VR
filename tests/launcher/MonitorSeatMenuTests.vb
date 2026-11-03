Imports DiRT3VR
Imports System.IO
Imports System.IO.MemoryMappedFiles
Imports System.Windows.Forms

' The in-game seat menu on the monitor (#c137c681, launcher side; Lens's renderer side on #35826889): the
' option, the desktop session that installs the renderer for it (journal schema 10), the channel's monitor
' fields (216 desktop, 220 launch FOV, 228 committed FOV, commit −3 = FOV only), RB/LB for the field of view.
Module MonitorSeatMenuTests
    Private Function Refused(action As Action) As Boolean
        Try
            action()
        Catch ex As IOException
            Return True
        End Try
        Return False
    End Function

    Private Function Context(root As String, name As String) As InstallContext
        Dim game = Path.Combine(root, "monitor-menu", name, "game")
        Dim documents = Path.Combine(root, "monitor-menu", name, "documents", "hardwaresettings")
        Directory.CreateDirectory(game) : Directory.CreateDirectory(documents)
        Dim ctx As New InstallContext(game, Path.Combine(root, "monitor-menu", name, "users"), Path.Combine(documents, "hardware_settings_config.xml"), Path.Combine(root, "monitor-menu", name, "package"))
        Directory.CreateDirectory(Path.Combine(game, "system")) : Directory.CreateDirectory(Path.Combine(ctx.PackageRoot, "payload"))
        File.WriteAllText(Path.Combine(ctx.PackageRoot, "payload", "d3d11.dll"), "owned test payload")
        Directory.CreateDirectory(Path.GetDirectoryName(SeatPositions.CameraPath(ctx, "mex")))
        File.WriteAllBytes(SeatPositions.CameraPath(ctx, "mex"), File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "fixtures", "seat", "cameras-comfort.bin")))
        Return ctx
    End Function

    Private Sub Commit(renderer As MemoryMappedViewAccessor, index As Integer, x As Single, y As Single, z As Single, fov As Integer)
        renderer.Write(92, renderer.ReadUInt32(92) + 1UI)
        renderer.Write(108, x) : renderer.Write(112, y) : renderer.Write(116, z)
        renderer.Write(SeatChannel.CommittedFovOffset, fov)
        renderer.Write(120, index) : renderer.Write(124, renderer.ReadUInt32(124) + 1UI)
        renderer.Write(92, renderer.ReadUInt32(92) + 1UI)
    End Sub

    Private Function Settings(Optional onDesktop As Boolean = True, Optional key As Integer = LauncherSettings.DefaultSeatMenuKey) As LauncherSettings
        Return New LauncherSettings With {.SteamRuntimeManifest = "", .VdxrRuntimeManifest = "", .SeatMenuOnDesktop = onDesktop, .SeatMenuKey = key}
    End Function

    Public Sub Run(root As String, check As Action(Of Boolean, String))
        ' The option: off by default, and only with a key or button.
        check(Not New LauncherSettings().SeatMenuOnDesktop AndAlso SeatChannel.OnDesktop(Settings()) AndAlso Not SeatChannel.OnDesktop(Settings(False)) AndAlso Not SeatChannel.OnDesktop(Settings(key:=0)),
              "the monitor menu needs the option and a key or button")
        check(DesktopStartupSession.NeedsSession(Settings()) AndAlso DesktopStartupSession.NeedsRenderer(Settings()) AndAlso Not DesktopStartupSession.NeedsRenderer(Settings(False)),
              "with it, Launch runs a managed session that installs the renderer")

        ' The channel's monitor fields.
        Dim frxIndex = EventCatalog.Current.Cars.ToList().FindIndex(Function(car) car.Code = "frx")
        Dim name = "Local\DiRT3VR.SeatTest." & Guid.NewGuid().ToString("N")
        Using channel As New SeatChannel(Settings(), Nothing, name, desktop:=True, launchFov:=10), opened = MemoryMappedFile.OpenExisting(name), renderer = opened.CreateViewAccessor()
            check(channel.Desktop AndAlso renderer.ReadUInt32(216) = 1UI AndAlso renderer.ReadInt32(220) = 10 AndAlso renderer.ReadInt32(224) = 10 AndAlso renderer.ReadInt32(228) = 10,
                  "a monitor channel says so and starts at the session's field of view (10° wider)")
            Commit(renderer, SeatChannel.FovOnlyIndex, 0, 0, 0, 15)
            Dim fovOnly = channel.ReadCommit()
            check(fovOnly IsNot Nothing AndAlso fovOnly.FovOnly AndAlso fovOnly.Fov.GetValueOrDefault(-99) = 15, "with no car identified a commit is the field of view alone")
            Commit(renderer, frxIndex, 0, 0.03F, 0, -5)
            Dim both = channel.ReadCommit()
            check(both IsNot Nothing AndAlso Not both.FovOnly AndAlso both.Car = "frx" AndAlso both.Offset.Up = 3 AndAlso both.Fov.GetValueOrDefault(-99) = -5, "a car's commit carries its seat and the field of view")
            Commit(renderer, frxIndex, 0, 0.03F, 0, 40)
            check(channel.ReadCommit() Is Nothing, "a field of view outside 20° narrower to 30° wider is ignored")
            Dim saved = Settings()
            SeatChannel.Apply(saved, fovOnly)
            check(saved.DesktopFov = 15 AndAlso (saved.SeatOffsets Is Nothing OrElse saved.SeatOffsets.Count = 0), "a field-of-view commit sets only Cockpit field of view (monitor)")
            SeatChannel.Apply(saved, both)
            check(saved.DesktopFov = -5 AndAlso saved.SeatOffsets("frx").Up = 3, "a car's commit sets both")
        End Using
        Dim vrName = "Local\DiRT3VR.SeatTest." & Guid.NewGuid().ToString("N")
        Using channel As New SeatChannel(Settings(), Nothing, vrName), opened = MemoryMappedFile.OpenExisting(vrName), renderer = opened.CreateViewAccessor()
            check(Not channel.Desktop AndAlso renderer.ReadUInt32(216) = 0UI AndAlso renderer.ReadInt32(220) = 0, "a VR channel leaves the monitor fields at zero")
            Commit(renderer, SeatChannel.FovOnlyIndex, 0, 0, 0, 15)
            check(channel.ReadCommit() Is Nothing, "VR never takes a field-of-view commit")
            Commit(renderer, frxIndex, 0, 0.01F, 0, 15)
            check(channel.ReadCommit()?.Fov Is Nothing, "and a VR seat commit carries no field of view")
        End Using

        ' RB and LB change the field of view, on the monitor only.
        Dim pad As New DrivingInput.Device(0UI, "xinput:0", "Xbox controller 1", "xinput_pad")
        Dim sample = DrivingInput.Sample.Empty() : sample.Connected = 1UI : sample.Buttons(9) = 1
        Dim monitor = SeatMenuInput.Read(pad, sample, Nothing, True, desktop:=True)
        check(monitor.Held = (1UI << SeatMenuInput.FovWiderBit) AndAlso (monitor.Buttons(0) And &H300UI) = &H300UI, "RB makes it wider, and RB and LB are kept from the game while the menu is open")
        sample.Buttons(9) = 0 : sample.Buttons(8) = 1
        check(SeatMenuInput.Read(pad, sample, Nothing, True, desktop:=True).Held = (1UI << SeatMenuInput.FovNarrowerBit), "LB makes it narrower")
        Dim vr = SeatMenuInput.Read(pad, sample, Nothing, True)
        check(vr.Held = 0UI AndAlso (vr.Buttons(0) And &H300UI) = 0UI, "in VR, LB and RB stay the game's")
        check(SeatMenuInput.Reserved(New RecenterDeviceBinding With {.DeviceId = "xinput:0", .DeviceName = "Xbox controller 1", .Product = "xinput_pad", .Input = "button:9"}) <> "", "RB can't open the menu")
        check(SeatMenuInput.Hint(Nothing, Nothing, desktop:=True).Contains("PgUp / RB: wider") AndAlso Not SeatMenuInput.Hint(Nothing, Nothing).Contains("PgUp"), "the monitor hint names the field-of-view keys")

        ' The session: the renderer without direct practice, schema 10, restored after exit.
        Dim ctx = Context(root, "session")
        Dim withSeat = Settings()
        withSeat.SeatOnDesktop = True : withSeat.DesktopFov = 10
        withSeat.SeatOffsets = New Dictionary(Of String, SeatOffset) From {{"mex", New SeatOffset With {.Up = 2}}}
        withSeat.Validate()
        Dim seats = SeatPositions.Changes(ctx, withSeat, vr:=False)
        Dim changes = DesktopStartupSession.Changes(ctx, withSeat, Nothing, seats.Files)
        check(changes.ContainsKey("proxy") AndAlso Not changes.ContainsKey("direct") AndAlso changes.ContainsKey("seat/mex"), "the session installs the renderer, without direct practice, with the seat")
        Dim transaction As New OwnedFileTransaction(ctx)
        transaction.Prepare(changes, apply:=False, seats:=seats.Offsets, seatMenu:=True)
        Dim journal = SafeFiles.ReadJson(Of RecoveryJournal)(transaction.JournalPath)
        check(journal.Schema = 10 AndAlso journal.Seats("mex").Up = 2 AndAlso journal.Seats("mex").Fov = 10, "a monitor seat menu session writes a schema 10 journal (older launchers refuse it)")
        Dim journalText = File.ReadAllText(transaction.JournalPath)
        File.WriteAllText(Path.Combine(ctx.GameRoot, "d3d11.dll"), "owned test payload")
        File.WriteAllBytes(SeatPositions.CameraPath(ctx, "mex"), changes("seat/mex"))
        Dim relabelled = System.Text.RegularExpressions.Regex.Replace(journalText, """Schema"":\s*10", """Schema"": 9")
        check(relabelled <> journalText, "the journal can be relabelled for the next check")
        File.WriteAllText(transaction.JournalPath, relabelled)
        check(Refused(Sub() transaction.Recover()) AndAlso File.Exists(Path.Combine(ctx.GameRoot, "d3d11.dll")), "relabelled as a desktop seat journal it is refused and the files kept")
        File.WriteAllText(transaction.JournalPath, journalText)
        check(transaction.Recover() AndAlso Not File.Exists(Path.Combine(ctx.GameRoot, "d3d11.dll")) AndAlso SeatCameras.ViewValues(File.ReadAllBytes(SeatPositions.CameraPath(ctx, "mex")), "head-cam")("fov") = "50.0000",
              "after the session the renderer is gone and the camera file is back")
        Dim plain As New OwnedFileTransaction(Context(root, "menu-only"))
        plain.Prepare(New Dictionary(Of String, Byte()) From {{"proxy", Text.Encoding.UTF8.GetBytes("owned test payload")}}, apply:=False, seatMenu:=True)
        check(SafeFiles.ReadJson(Of RecoveryJournal)(plain.JournalPath).Schema = 10 AndAlso SafeFiles.ReadJson(Of RecoveryJournal)(plain.JournalPath).Seats Is Nothing, "the renderer alone is a schema 10 session too")
        check(plain.Recover(), "and it is restored")
        Dim vrMenu As New OwnedFileTransaction(Context(root, "vr-menu"))
        check(Refused(Sub() vrMenu.Prepare(New Dictionary(Of String, Byte()) From {{"proxy", Text.Encoding.UTF8.GetBytes("owned test payload")}}, experimental:=True, apply:=False, seatMenu:=True)), "the monitor menu belongs to a desktop session")

        ' The game's environment: the channel and the log folder; not direct practice.
        Dim logs = Path.Combine(ctx.UserRoot, "logs", "session-fixture")
        Dim start = DesktopStartupSession.StartInfo(ctx, Settings(), logs, Nothing, "Local\DiRT3VR.Seat.test")
        check(start.Environment(SeatChannel.EnvironmentName) = "Local\DiRT3VR.Seat.test" AndAlso start.Environment("DIRT3VR_LOG_DIR") = logs AndAlso Not start.Environment.ContainsKey("DIRT3VR_DIRECT_PRACTICE") AndAlso start.ArgumentList.Count = 0,
              "a normal Launch with the menu passes the channel and the log folder, and starts DiRT 3 as usual")
        Environment.SetEnvironmentVariable(SeatChannel.EnvironmentName, "stale")
        Try
            Dim none = DesktopStartupSession.StartInfo(ctx, Settings(False), logs)
            check(Not none.Environment.ContainsKey(SeatChannel.EnvironmentName) AndAlso Not none.Environment.ContainsKey("DIRT3VR_LOG_DIR"), "without the menu nothing is passed, even if inherited")
        Finally
            Environment.SetEnvironmentVariable(SeatChannel.EnvironmentName, Nothing)
        End Try
    End Sub

    Public Sub RunUi(root As String, check As Action(Of Boolean, String))
        Dim game = Path.Combine(root, "monitor-menu-ui")
        Directory.CreateDirectory(game)
        Dim ctx As New InstallContext(game, Path.Combine(root, "monitor-menu-ui-users"), Path.Combine(game, "graphics.xml"), root)
        Call New LauncherSettings With {.SteamRuntimeManifest = "", .VdxrRuntimeManifest = ""}.Save(ctx)
        Using form As New MainForm(ctx)
            form.ShowInTaskbar = False : form.StartPosition = FormStartPosition.Manual : form.Location = New Drawing.Point(-30000, -30000)
            form.Show() : Application.DoEvents()
            Dim box = DirectCast(form.Controls.Find("SeatMenuOnDesktop", True).Single(), CheckBox)
            check(Not box.Checked AndAlso Not form.CurrentSettings().SeatMenuOnDesktop AndAlso Not form.HasUnsavedSettings(), "Controls → Seat: the monitor menu is off by default")
            box.Checked = True : Application.DoEvents()
            check(form.CurrentSettings().SeatMenuOnDesktop AndAlso form.HasUnsavedSettings(), "turning it on is a launcher setting")
            DirectCast(form.Controls.Find("SaveSettings", True).Single(), Button).PerformClick()
            check(LauncherSettings.Load(ctx).SeatMenuOnDesktop, "Save settings keeps it")
            form.ShowSeatCommit(New SeatCommit With {.FovOnly = True, .Fov = 12})
            Dim notice = form.Controls.Find("Notice", True).Single()
            check(form.CurrentSettings().DesktopFov = 12 AndAlso DirectCast(form.Controls.Find("SeatDesktopFov", True).Single(), ValueSlider).Value = 12 AndAlso notice.Text.Contains("12° wider"),
                  "a field of view saved in the monitor menu shows in Controls → Seat (" & notice.Text & ")")
            form.Close()
        End Using
    End Sub
End Module
