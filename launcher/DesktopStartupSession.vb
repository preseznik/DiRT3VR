' Desktop launches with temporary game-folder changes need a process-lifetime owner: startup logo
' skipping, seat positions on the monitor, direct practice, which installs DiRT3VR's renderer (as a
' plain pass-through, no VR) for its player-control fix, the in-game seat menu on the monitor, which
' installs it for the menu (#c137c681), anisotropic filtering other than the game's (#15714dd2), and bloom or
' light streaks + lens flares turned off (#3c6d6ffa), which edit DiRT 3's effect files for the session.
' Ordinary desktop launching remains unchanged and never installs the renderer.
Public NotInheritable Class DesktopStartupSession
    Private ReadOnly context As InstallContext
    Private ReadOnly lease As FileStream
    Public ReadOnly Game As Process
    ' Devices whose launcher driving bindings couldn't go into the session's action maps, or "".
    Public Property DrivingNote As String = ""
    ' Bloom or light streaks + lens flares that stay on because an effect file is missing or unexpected, or "".
    Public Property EffectsNote As String = ""
    ' Cars whose seat position couldn't be applied (camera file missing or not the expected format).
    Public Property SeatsSkipped As New List(Of String)
    ' The in-game seat menu's shared memory for this session (monitor menu), or Nothing.
    Public Property SeatMenu As SeatChannel
    ' A seat position or field of view saved from the menu just before DiRT 3 closed, for the window to show.
    Public Property FinalSeatCommit As SeatCommit
    ' The session's log folder (direct practice or the seat menu), or "".
    Public Property LogDirectory As String = ""

    Private Sub New(install As InstallContext, child As Process, sessionLease As FileStream)
        context = install : Game = child : lease = sessionLease
    End Sub

    ' session.json mode of a desktop direct practice log folder.
    Public Const DirectMode As String = "desktop-direct-practice"
    ' session.json mode of a desktop session with only the seat menu's renderer.
    Public Const SeatMenuMode As String = "desktop-seat-menu"
    ' session.json mode of a desktop session with the renderer only for anisotropic filtering.
    Public Const RendererMode As String = "desktop-renderer"

    Public Shared Function NeedsSession(settings As LauncherSettings) As Boolean
        Return settings.SkipStartupMovies OrElse settings.LaunchMode = "direct" OrElse SeatPositions.OnDesktop(settings) OrElse NeedsRenderer(settings) OrElse
               Not settings.DesktopBloom OrElse Not settings.DesktopGlare OrElse Not settings.DesktopColourFilter
    End Function

    ' Whether the session installs DiRT3VR's renderer: direct practice, the seat menu on the monitor,
    ' anisotropic filtering other than the game's, or the steering animation correction.
    Public Shared Function NeedsRenderer(settings As LauncherSettings) As Boolean
        Return settings.LaunchMode = "direct" OrElse SeatChannel.OnDesktop(settings) OrElse settings.DesktopAnisotropy <> 0 OrElse settings.CockpitSteeringShakeOff
    End Function

    ' The temporary changes: the startup definitions and/or the direct practice config plus the renderer,
    ' the launcher's driving bindings as session action maps (#8121970e), and the seat positions' camera
    ' files when they apply on the monitor (#b330cc4f).
    Public Shared Function Changes(context As InstallContext, settings As LauncherSettings) As Dictionary(Of String, Byte())
        Return Changes(context, settings, Nothing, Nothing)
    End Function

    Public Shared Function Changes(context As InstallContext, settings As LauncherSettings, actionMaps As SessionActionMaps.SessionChanges, seatFiles As IReadOnlyDictionary(Of String, Byte())) As Dictionary(Of String, Byte())
        Dim result As New Dictionary(Of String, Byte())
        If settings.SkipStartupMovies Then result.Add("startup", StartupMovies.ReadPatch(context))
        If NeedsRenderer(settings) Then
            Dim payload = Path.Combine(context.PackageRoot, "payload", "d3d11.dll")
            SafeFiles.NoLinks(payload)
            If Not File.Exists(payload) Then Throw New IOException("DiRT3VR's renderer is missing from this package: " & payload)
            result.Add("proxy", File.ReadAllBytes(payload))
        End If
        If settings.LaunchMode = "direct" Then
            result.Add("direct", DirectPractice.Config(settings))
            Dim maps = If(actionMaps, SessionActionMaps.Changes(context, DrivingControls.Load(context)))
            For Each pair In maps.Files
                result.Add(pair.Key, pair.Value)
            Next
        End If
        For Each pair In If(seatFiles, SeatPositions.Changes(context, settings, vr:=False).Files)
            result.Add(pair.Key, pair.Value)
        Next
        Return result
    End Function

    ' Direct practice on the desktop: the benchmark start with the player-control fix and laps. The
    ' renderer only observes presentation (DIRT3VR_DIAGNOSTICS, needed for the lap count) and logs.
    ' seatChannelName: the monitor seat menu's shared memory (its desktop flag marks the monitor session).
    Public Shared Function StartInfo(context As InstallContext, settings As LauncherSettings, logDirectory As String, Optional directDevices As IReadOnlyList(Of String) = Nothing, Optional seatChannelName As String = Nothing) As ProcessStartInfo
        Dim start = Session.DesktopStartInfo(context)
        If seatChannelName IsNot Nothing Then
            start.Environment(SeatChannel.EnvironmentName) = seatChannelName
        Else
            start.Environment.Remove(SeatChannel.EnvironmentName)
        End If
        Anisotropy.Configure(start, settings.DesktopAnisotropy)
        Session.ConfigureSteeringShake(start, settings)
        If settings.LaunchMode <> "direct" AndAlso seatChannelName Is Nothing AndAlso settings.DesktopAnisotropy = 0 AndAlso Not settings.CockpitSteeringShakeOff Then Return start
        If Not Path.IsPathFullyQualified(logDirectory) Then Throw New IOException("The session log path must be absolute.")
        start.Environment("DIRT3VR_LOG_DIR") = logDirectory
        ' The renderer runs the monitor menu, like direct practice's lap count, from its Present hook; anisotropic
        ' filtering and the steering animation correction need it too.
        start.Environment("DIRT3VR_DIAGNOSTICS") = "1"
        If settings.LaunchMode <> "direct" Then Return start
        DirectPractice.AddStart(start, settings, directDevices)
        Return start
    End Function

    Public Shared Async Function StartAsync(context As InstallContext, settings As LauncherSettings, monitor As Drawing.Size) As Task(Of DesktopStartupSession)
        If Not NeedsSession(settings) Then Throw New ArgumentException("This session needs startup logo skipping, direct practice, seat positions or the seat menu on the monitor.")
        Dim lease = InteractiveSession.AcquireLease(context), game As Process = Nothing
        Dim transaction As New OwnedFileTransaction(context), preparing = False
        Dim failure As Exception = Nothing
        Dim seatMenu As SeatChannel = Nothing
        Try
            context.RequireClosed() : context.ValidateGame() : settings.Validate()
            If transaction.Pending Then Throw New IOException("Restore pending session files before launching.")
            If NeedsRenderer(settings) Then
                ' DiRT 3 inherits the launcher's image-load rule; refuse if it would skip DiRT3VR's d3d11.dll.
                Dim blocked = RendererLoad.LaunchBlock(context)
                If blocked <> "" Then Throw New IOException(blocked)
            End If
            Dim actionMaps = If(settings.LaunchMode = "direct", SessionActionMaps.Changes(context, DrivingControls.Load(context)), New SessionActionMaps.SessionChanges())
            Dim seats = SeatPositions.Changes(context, settings, vr:=False)
            Dim changes = DesktopStartupSession.Changes(context, settings, actionMaps, seats.Files)
            ' Bloom, light streaks + lens flares (#3c6d6ffa) and the colour filter (#24f76866) off: the effect files and
            ' palettes, for this session only.
            Dim effectsNote As String = ""
            Dim post = PostEffects.SessionChanges(context, motionBlurOff:=False, glareOff:=Not settings.DesktopGlare, bloomOff:=Not settings.DesktopBloom, note:=effectsNote, colourOff:=Not settings.DesktopColourFilter)
            For Each pair In post.Files
                changes.Add(pair.Key, pair.Value)
            Next
            Dim logs = Path.Combine(context.UserRoot, "logs", "session-" & Guid.NewGuid().ToString("N"))
            If settings.LaunchMode <> "direct" AndAlso NeedsRenderer(settings) Then
                SafeFiles.NoLinks(logs) : Directory.CreateDirectory(logs)
                SafeFiles.SaveJson(Path.Combine(logs, "session.json"), New With {.schema = 1, .mode = If(SeatChannel.OnDesktop(settings), SeatMenuMode, RendererMode), .startedUtc = DateTime.UtcNow, .launchMode = settings.LaunchMode,
                                                                                .desktopFov = settings.DesktopFov, .seatCars = seats.Offsets.Keys.ToArray(), .desktopAnisotropy = settings.DesktopAnisotropy, .bloomOff = Not settings.DesktopBloom, .glareOff = Not settings.DesktopGlare, .colourOff = Not settings.DesktopColourFilter, .palettesSwapped = post.Files.ContainsKey(PostEffects.PaletteTarget)})
            End If
            If settings.LaunchMode = "direct" Then
                SafeFiles.NoLinks(logs) : Directory.CreateDirectory(logs)
                ' Marks the folder as a desktop session, so Last VR session skips it.
                SafeFiles.SaveJson(Path.Combine(logs, "session.json"), New With {.schema = 1, .mode = DirectMode, .startedUtc = DateTime.UtcNow, .launchMode = settings.LaunchMode, .directRoute = settings.DirectRoute, .directCar = settings.DirectCar, .directLaps = DirectPractice.Laps(settings), .directOpponents = DirectPractice.Opponents(settings), .directOpponentCars = settings.DirectOpponentCars, .directDifficulty = DirectPractice.Difficulty(settings), .directAfterFinish = settings.DirectAfterFinish, .directAssists = DirectPractice.Assists(settings), .drivingMaps = actionMaps.Devices.Select(Function(d) d.DeviceName & " (" & d.BaseFile & ")").ToArray(), .drivingSkipped = actionMaps.Skipped.ToArray(), .directDevices = actionMaps.PreferredDevices, .desktopAnisotropy = settings.DesktopAnisotropy, .bloomOff = Not settings.DesktopBloom, .glareOff = Not settings.DesktopGlare, .colourOff = Not settings.DesktopColourFilter, .palettesSwapped = post.Files.ContainsKey(PostEffects.PaletteTarget)})
            End If
            settings.Save(context)
            DesktopGraphics.Apply(context, settings, monitor)
            preparing = True
            ' Seat positions or effects alone may come to nothing (every file unusable or already as wanted).
            If changes.Count > 0 Then
                transaction.Prepare(changes, apply:=False, seats:=If(seats.Offsets.Count > 0, seats.Offsets, Nothing), monitorRenderer:=NeedsRenderer(settings), effects:=post.Edits)
                Await RecoveryWorker.ApplyAsync(context)
            End If
            context.RequireClosed()
            ' The monitor seat menu: the positions and field of view the camera files now hold are its starting point.
            If SeatChannel.OnDesktop(settings) Then
                seatMenu = New SeatChannel(settings, seats.Offsets, desktop:=True, launchFov:=If(seats.Offsets.Values.Any(Function(offset) offset.Fov <> 0), settings.DesktopFov, 0))
            End If
            game = Process.Start(StartInfo(context, settings, logs, actionMaps.PreferredDevices, seatMenu?.Name))
            If game Is Nothing Then Throw New IOException("DiRT 3 could not start.")
            Session.WriteLaunchStatus(context, "starting", "Waiting for the game window. DiRT3VR's temporary files are removed after exit.", game.Id, DateTime.UtcNow, mode:=If(settings.LaunchMode = "direct", "direct-practice", "desktop"))
            Return New DesktopStartupSession(context, game, lease) With {.DrivingNote = actionMaps.Note, .EffectsNote = effectsNote, .SeatsSkipped = seats.Skipped, .SeatMenu = seatMenu, .LogDirectory = If(NeedsRenderer(settings), logs, "")}
        Catch ex As Exception
            failure = ex
        End Try
        ' Once started, an error recording progress must never abandon the child.
        If game IsNot Nothing Then Return New DesktopStartupSession(context, game, lease) With {.SeatMenu = seatMenu}
        seatMenu?.Dispose()
        Try
            If preparing AndAlso transaction.Pending Then Await RecoveryWorker.RecoverAsync(context)
        Catch recoveryError As Exception
            Throw New IOException(failure.Message & " Recovery is pending: " & recoveryError.Message, failure)
        Finally
            lease.Dispose()
        End Try
        Throw failure
    End Function

    ' End session from the notification area: ask DiRT 3 to close, as its window's close button does.
    Public Function RequestExit() As String
        Try
            If Not Game.HasExited Then Game.CloseMainWindow()
        Catch ex As InvalidOperationException
            ' Already exited; the restore below still runs.
        End Try
        Return "Exit requested. DiRT3VR's temporary files are removed after DiRT 3 closes."
    End Function

    Public Async Function WaitAndRestoreAsync() As Task(Of String)
        Try
            Await Game.WaitForExitAsync()
            Dim exitCode = Game.ExitCode
            ' A seat position or field of view saved in the menu just before DiRT 3 closed is kept too.
            Try
                FinalSeatCommit = SeatChannel.SaveCommit(context, SeatMenu)
            Catch ex As IOException
            End Try
            SeatMenu?.Dispose() : SeatMenu = Nothing
            DirectPractice.RemoveResults(context)
            ' Another DiRT 3 process still closing gets up to 30 s; after that the restore names it and refuses.
            Await InstallContext.WaitUntilClosedAsync(InstallContext.CloseGrace)
            Await RecoveryWorker.RecoverAsync(context)
            Return "DiRT 3 closed (exit code 0x" & exitCode.ToString("X8") & "). DiRT3VR's temporary files were removed."
        Finally
            Game.Dispose() : lease.Dispose()
        End Try
    End Function
End Class
