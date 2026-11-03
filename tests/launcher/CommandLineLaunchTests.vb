Imports DiRT3VR
Imports System.IO

' DiRT3VR.exe --launch [--vr] (Lens's ask for fake-headset runs, #0ea87e54): the saved settings, the runtime from
' --runtime / --manifest or XR_RUNTIME_JSON for this launch only, errors on standard error with exit code 1.
Module CommandLineLaunchTests
    Private Function Context(root As String, name As String) As InstallContext
        Dim game = Path.Combine(root, "command-line", name)
        Directory.CreateDirectory(game)
        Return New InstallContext(game, Path.Combine(root, "command-line-users", name), Path.Combine(game, "graphics.xml"), Path.Combine(root, "command-line", name, "package"))
    End Function

    Public Sub Run(root As String, check As Action(Of Boolean, String))
        Dim ctx = Context(root, "settings")
        Call New LauncherSettings With {.SteamRuntimeManifest = "C:\Steam\steamxr_win32.json", .VdxrRuntimeManifest = "C:\VD\virtualdesktop-openxr-32.json", .VrRenderScale = 90}.Save(ctx)
        Dim plain = CommandLineLaunch.SettingsFor(ctx, {"--launch", "--vr"}, Nothing)
        check(Not plain.Overridden AndAlso plain.Settings.RuntimeMode = "vdxr" AndAlso plain.Settings.VrRenderScale = 90, "without a runtime given, the saved settings are used as they are")
        Dim fromEnvironment = CommandLineLaunch.SettingsFor(ctx, {"--launch", "--vr"}, "D:\SteamVR\steamxr_win32.json")
        check(fromEnvironment.Overridden AndAlso fromEnvironment.Settings.RuntimeMode = "other" AndAlso fromEnvironment.Settings.OtherRuntimeManifest = "D:\SteamVR\steamxr_win32.json" AndAlso fromEnvironment.Settings.VrRenderScale = 90,
              "XR_RUNTIME_JSON becomes the other OpenXR runtime for this launch; the rest is the saved settings")
        Dim explicitRuntime = CommandLineLaunch.SettingsFor(ctx, {"--launch", "--vr", "--runtime", "steamvr", "--manifest", "E:\steamxr_win32.json"}, "D:\ignored.json")
        check(explicitRuntime.Overridden AndAlso explicitRuntime.Settings.RuntimeMode = "steamvr" AndAlso explicitRuntime.Settings.SteamRuntimeManifest = "E:\steamxr_win32.json", "--runtime and --manifest win over XR_RUNTIME_JSON")
        Dim refused = False
        Try
            CommandLineLaunch.SettingsFor(ctx, {"--launch", "--runtime"}, Nothing)
        Catch ex As ArgumentException
            refused = True
        End Try
        check(refused, "a missing value is refused")

        ' Errors: exit code 1, the reason on standard error, and the saved settings untouched.
        Dim before = File.ReadAllBytes(ctx.PreferencesPath)
        Dim output As New StringWriter(), errors As New StringWriter()
        Environment.SetEnvironmentVariable(CommandLineLaunch.RuntimeVariable, "D:\SteamVR\steamxr_win32.json")
        Try
            check(CommandLineLaunch.Run(ctx, {"--launch", "--vr"}, output, errors) = 1 AndAlso errors.ToString().Contains("Enable Experimental VR") AndAlso output.ToString() = "",
                  "without the experimental VR opt-in it stops with that reason (" & errors.ToString().Trim() & ")")
            Dim optedIn = LauncherSettings.Load(ctx) : optedIn.ExperimentalVrEnabled = True : optedIn.Save(ctx)
            before = File.ReadAllBytes(ctx.PreferencesPath)
            errors = New StringWriter()
            check(CommandLineLaunch.Run(ctx, {"--launch", "--vr"}, output, errors) = 1 AndAlso errors.ToString().Length > 0, "a package without VR for that runtime stops too (" & errors.ToString().Trim() & ")")
            check(File.ReadAllBytes(ctx.PreferencesPath).SequenceEqual(before), "the saved settings keep their own runtime")
        Finally
            Environment.SetEnvironmentVariable(CommandLineLaunch.RuntimeVariable, Nothing)
        End Try
    End Sub
End Module
