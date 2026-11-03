Imports System.Drawing

' DiRT3VR.exe --launch [--vr] [--runtime vdxr|steamvr|other --manifest <path>]: a session with this PC's saved launcher
' settings and no window, as DiRT2VR's --launch. It waits for DiRT 3 to close and its files to be restored, writes
' progress to standard output and errors to standard error, and returns 0, or 1 on an error. For automated VR runs on
' SteamVR's virtual headset (Lens's tools/fake_headset.py, #0ea87e54): with XR_RUNTIME_JSON set and no --runtime, that
' manifest is used as the other OpenXR runtime for this launch only. The saved settings keep their own runtime.
' Controller and wheel buttons for recenter, Toggle VR and the seat menu need the launcher window; keys work.
Public Module CommandLineLaunch
    Public Const RuntimeVariable As String = "XR_RUNTIME_JSON"

    ' This launch's settings: the saved ones with the runtime from --runtime / --manifest, else from XR_RUNTIME_JSON
    ' as the other OpenXR runtime. Overridden says whether the runtime differs from the saved one.
    Public Function SettingsFor(context As InstallContext, arguments As String(), environmentRuntime As String) As (Settings As LauncherSettings, Overridden As Boolean)
        Dim settings = LauncherSettings.Load(context)
        Dim overridden = False
        Dim mode = Argument(arguments, "--runtime", Nothing)
        If mode IsNot Nothing Then
            settings.RuntimeMode = mode
            Dim manifest = Argument(arguments, "--manifest", Nothing)
            If manifest IsNot Nothing Then
                Select Case mode
                    Case "steamvr" : settings.SteamRuntimeManifest = manifest
                    Case "other" : settings.OtherRuntimeManifest = manifest
                    Case Else : settings.VdxrRuntimeManifest = manifest
                End Select
            End If
            overridden = True
        ElseIf Not String.IsNullOrWhiteSpace(environmentRuntime) Then
            settings.RuntimeMode = "other" : settings.OtherRuntimeManifest = environmentRuntime
            overridden = True
        End If
        settings.Validate()
        Return (settings, overridden)
    End Function

    Public Function Run(context As InstallContext, arguments As String(), output As TextWriter, errors As TextWriter) As Integer
        Dim saved As Byte() = Nothing, restored = False
        ' Starting a session saves its settings; a runtime given only for this launch must not stay in them.
        Dim putBack = Sub()
                          If restored OrElse saved Is Nothing Then Return
                          restored = True
                          SafeFiles.WriteAtomic(context.PreferencesPath, saved)
                      End Sub
        Try
            Dim chosen = SettingsFor(context, arguments, Environment.GetEnvironmentVariable(RuntimeVariable))
            If chosen.Overridden AndAlso File.Exists(context.PreferencesPath) Then saved = File.ReadAllBytes(context.PreferencesPath)
            Dim settings = chosen.Settings
            If arguments.Contains("--vr") Then
                Dim vrSession As InteractiveSession
                Try
                    vrSession = InteractiveSession.StartAsync(context, settings).GetAwaiter().GetResult()
                Finally
                    putBack()
                End Try
                output.WriteLine("DiRT 3 started in VR (process " & vrSession.Game.Id.ToString(Globalization.CultureInfo.InvariantCulture) & "). Session logs: " & vrSession.LogDirectory)
                output.Flush()
                output.WriteLine(vrSession.WaitAndRestoreAsync().GetAwaiter().GetResult())
            Else
                Dim monitor = If(settings.DesktopDisplayMode = "borderless", DesktopDisplay.MainBounds().Size, Size.Empty)
                If DesktopStartupSession.NeedsSession(settings) Then
                    Dim desktopSession As DesktopStartupSession
                    Try
                        desktopSession = DesktopStartupSession.StartAsync(context, settings, monitor).GetAwaiter().GetResult()
                    Finally
                        putBack()
                    End Try
                    output.WriteLine("DiRT 3 started (process " & desktopSession.Game.Id.ToString(Globalization.CultureInfo.InvariantCulture) & ")." & If(desktopSession.LogDirectory = "", "", " Session logs: " & desktopSession.LogDirectory))
                    output.Flush()
                    output.WriteLine(desktopSession.WaitAndRestoreAsync().GetAwaiter().GetResult())
                Else
                    Dim game As Process
                    Try
                        game = Session.LaunchDesktop(context, settings, monitor)
                    Finally
                        putBack()
                    End Try
                    Using game
                        output.WriteLine("DiRT 3 started (process " & game.Id.ToString(Globalization.CultureInfo.InvariantCulture) & ").")
                        output.Flush()
                        game.WaitForExit()
                        output.WriteLine("DiRT 3 closed (exit code 0x" & game.ExitCode.ToString("X8", Globalization.CultureInfo.InvariantCulture) & ").")
                    End Using
                End If
            End If
            Return 0
        Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException OrElse TypeOf ex Is InvalidOperationException OrElse TypeOf ex Is ComponentModel.Win32Exception OrElse TypeOf ex Is ArgumentException
            errors.WriteLine(ex.Message)
            Return 1
        Finally
            Try
                putBack()
            Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException
                errors.WriteLine("The saved launcher settings could not be put back: " & ex.Message)
            End Try
        End Try
    End Function

    Private Function Argument(arguments As String(), name As String, fallback As String) As String
        Dim index = Array.IndexOf(arguments, name)
        If index < 0 Then Return fallback
        If index + 1 >= arguments.Length Then Throw New ArgumentException("Missing value for " & name & ".")
        Return arguments(index + 1)
    End Function
End Module
