Imports DiRT3VR
Imports System.IO
Imports System.Diagnostics
Imports System.Windows.Forms

Module StartupTests
    Private Sub Reject(action As Action, check As Action(Of Boolean, String), message As String)
        Dim rejected = False
        Try
            action()
        Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is ArgumentException
            rejected = True
        End Try
        check(rejected, message)
    End Sub

    Public Sub Run(root As String, check As Action(Of Boolean, String))
        Dim fixture = Path.Combine(AppContext.BaseDirectory, "fixtures", "startup-states.bin")
        If Not File.Exists(fixture) Then Throw New IOException("Generate the local startup fixture using tools/research/inspect_startup_movies.py before running launcher tests.")
        Dim original = File.ReadAllBytes(fixture), patched = StartupMovies.Patch(original)
        check(SafeFiles.HashBytes(original) = StartupMovies.OriginalHash, "startup patch leaves its original input unchanged")
        check(SafeFiles.HashBytes(patched) = StartupMovies.AppliedHash AndAlso patched.Length = original.Length, "startup output matches independently decoded candidate")
        Dim offsets = {130396, 130404, 433468, 433472, 130420, 130428, 433500, 433504}
        Dim allowed = offsets.SelectMany(Function(start) Enumerable.Range(start, 4)).ToHashSet()
        check(Enumerable.Range(0, original.Length).All(Function(index) original(index) = patched(index) OrElse allowed.Contains(index)), "only verified startup element and attribute words differ")
        Dim mutated = DirectCast(original.Clone(), Byte())
        mutated(1000) = CByte(mutated(1000) Xor 1)
        For Each invalid In {Array.Empty(Of Byte)(), original.Take(20).ToArray(), mutated, patched}
            Reject(Sub() StartupMovies.Patch(invalid), check, "unsupported or already-patched startup definitions refused")
        Next
        Dim game = Path.Combine(root, "startup-option")
        Directory.CreateDirectory(Path.Combine(game, "system"))
        Dim ctx As New InstallContext(game, Path.Combine(root, "startup-users"), Path.Combine(game, "graphics.xml"), root)
        Dim settings = LauncherSettings.Load(ctx)
        check(Not settings.SkipStartupMovies, "older/default preferences keep startup movies")
        Directory.CreateDirectory(ctx.UserRoot)
        File.WriteAllText(ctx.PreferencesPath, "{""Version"":1,""VrRenderScale"":75}")
        check(Not LauncherSettings.Load(ctx).SkipStartupMovies, "old settings JSON keeps default startup behavior")
        Dim target = Path.Combine(game, "system", "states.bin")
        File.WriteAllBytes(target, original)
        settings.SkipStartupMovies = True : settings.Save(ctx)
        check(LauncherSettings.Load(ctx).SkipStartupMovies AndAlso SafeFiles.Hash(target) = StartupMovies.OriginalHash, "saving startup choice does not edit installed definitions")
        check(StartupMovies.ReadPatch(ctx).SequenceEqual(patched) AndAlso SafeFiles.Hash(target) = StartupMovies.OriginalHash, "startup candidate preparation is read-only")
        Reject(Sub() Session.LaunchDesktop(ctx, settings), check, "startup edits cannot bypass managed desktop recovery")
        File.WriteAllBytes(target, mutated)
        Reject(Sub() StartupMovies.ReadPatch(ctx), check, "modified startup asset is preserved and refused")
        check(File.ReadAllBytes(target).SequenceEqual(mutated), "refused startup asset remains intact")
        StartupRecoveryTests.Run(root, original, check)
    End Sub

    Public Sub RunWindowLifecycle(root As String, check As Action(Of Boolean, String))
        Dim game = Path.Combine(root, "startup-lifetime"), original = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "fixtures", "startup-states.bin"))
        Directory.CreateDirectory(Path.Combine(game, "system"))
        Dim ctx As New InstallContext(game, Path.Combine(root, "startup-lifetime-users"), Path.Combine(game, "graphics.xml"), root)
        Dim target = Path.Combine(game, "system", "states.bin"), transaction As New OwnedFileTransaction(ctx)
        File.WriteAllBytes(target, original)
        transaction.Prepare(New Dictionary(Of String, Byte()) From {{"startup", StartupMovies.Patch(original)}}, apply:=False)
        File.WriteAllBytes(target, StartupMovies.Patch(original))
        ' This controlled child is the test executable's sleep-and-exit branch,
        ' not a game. Exercise real Process handle ownership and exit recovery.
        Dim start As New ProcessStartInfo(Environment.ProcessPath) With {.UseShellExecute = False, .CreateNoWindow = True}
        start.ArgumentList.Add("--startup-test-child")
        Using child = Process.Start(start), lease = InteractiveSession.AcquireLease(ctx), parent As New Form()
            Dim constructor = GetType(DesktopStartupSession).GetConstructors(Reflection.BindingFlags.Instance Or Reflection.BindingFlags.NonPublic).Single()
            Dim session = DirectCast(constructor.Invoke({ctx, child, lease}), DesktopStartupSession)
            Using monitor As New LaunchProgressMonitor(parent, child, ctx, disposeProcess:=False)
                monitor.Dispose()
            End Using
            check(child.WaitForExit(5000), "disposing startup progress preserves the lifetime owner's process handle")
            Dim result = System.Threading.Tasks.Task.Run(Function() session.WaitAndRestoreAsync()).GetAwaiter().GetResult()
            check(result.Contains("restored") AndAlso Not transaction.Pending AndAlso SafeFiles.Hash(target) = StartupMovies.OriginalHash, "desktop process exit restores original startup definitions")
            Using nextLease = InteractiveSession.AcquireLease(ctx)
                check(True, "desktop exit recovery releases session ownership")
            End Using
        End Using
    End Sub
End Module
