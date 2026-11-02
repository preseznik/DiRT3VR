Imports DiRT3VR
Imports System.Drawing
Imports System.IO
Imports System.Text
Imports System.Text.Json
Imports System.Windows.Forms

Module Program
    Private checks As Integer

    <STAThread>
    Function Main(args As String()) As Integer
        If args.SequenceEqual({"--startup-test-child"}) Then
            System.Threading.Thread.Sleep(250)
            Return 0
        End If
        Dim root = Path.Combine(AppContext.BaseDirectory, "test-artifacts", Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(root)
        Try
            TestSettings(root)
            DesktopTests.Run(root, AddressOf Check)
            ProfileTests.Run(root, AddressOf Check)
            RecenterTests.Run(root, AddressOf Check)
            DrivingInputTests.Run(root, AddressOf Check)
            UpdateTests.Run(root, AddressOf Check)
            VrLayerTests.Run(root, AddressOf Check)
            RendererLoadTests.Run(root, AddressOf Check)
            RecenterDeviceTests.Run(root, AddressOf Check)
            BackgroundTests.Run(root, AddressOf Check)
            RecoveryConflictTests.Run(root, AddressOf Check)
            DefaultsTests.Run(root, AddressOf Check)
            LogCleanupTests.Run(root, AddressOf Check)
            LastVrSessionTests.Run(root, AddressOf Check)
            RecenterTiltTests.Run(root, AddressOf Check)
            GraphicsQualityTests.Run(root, AddressOf Check)
            DirectPracticeTests.Run(root, AddressOf Check)
            NewFeaturesTests.Run(root, AddressOf Check)
            SeatTests.Run(root, AddressOf Check)
            MotionBlurTests.Run(root, AddressOf Check)
            DrivingControlsTests.Run(root, AddressOf Check)
            SessionActionMapsTests.Run(root, AddressOf Check)
            StartupTests.Run(root, AddressOf Check)
            InstallerTests.Run(root, AddressOf Check)
            TestHeadsetResolution(root)
            TestWindowPreferences(root)
            TestRecovery(root)
            TestRuntime(root)
            TestLaunch(root)
            TestExperimental(root)
            TestLaunchProgress()
            If args.Contains("--ui") Then TestAppearance(root, DiRT3VR.Program.Argument(args, "--package", Nothing))
            Console.WriteLine($"PASS: {checks} launcher checks. Artifacts: {root}")
            Return 0
        Catch ex As Exception
            Console.Error.WriteLine(ex.ToString())
            Return 1
        End Try
    End Function

    Private Sub Check(value As Boolean, message As String)
        checks += 1
        If Not value Then Throw New Exception("FAIL: " & message)
    End Sub

    Private Sub TestExperimental(root As String)
        Dim original = Encoding.UTF8.GetBytes("<hardware_settings_config version='56'><cpu><threadStrategy parallelUpdateRender='true' workerMapFile='keep.xml'/></cpu><graphics_card><directx forcedx9='true'/><stereo enabled='true'/><resolution width='1920' height='1080' fullscreen='true' vsync='1' multisampling='4xmsaa' extra='keep'/></graphics_card><audio mixing='original'/></hardware_settings_config>")
        Dim applied = ExperimentalFiles.Graphics(original)
        Check(Encoding.UTF8.GetString(applied).Contains("workerMapFile=""keep.xml""") AndAlso Encoding.UTF8.GetString(applied).Contains("mixing=""original"""), "experimental profile preserves unrelated settings")
        Check(Encoding.UTF8.GetString(applied).Contains("width=""1280""") AndAlso Encoding.UTF8.GetString(applied).Contains("parallelUpdateRender=""false"""), "experimental profile uses tested graphics and scheduling")
        Dim formatted = Encoding.UTF8.GetBytes(System.Xml.Linq.XDocument.Parse(Encoding.UTF8.GetString(applied)).ToString())
        Check(ExperimentalFiles.SameXml(applied, formatted), "format-only graphics serialization is equivalent")
        Check(Not ExperimentalFiles.SameXml(original, applied), "a real settings change is not formatting")
        For Each edited In {"<!-- user note -->" & Encoding.UTF8.GetString(applied), "<?user value?>" & Encoding.UTF8.GetString(applied), Encoding.UTF8.GetString(applied).Replace("</graphics_card>", "<!-- user note --></graphics_card>")}
            Check(Not ExperimentalFiles.SameXml(applied, Encoding.UTF8.GetBytes(edited)), "comment and processing-instruction additions are user edits")
        Next
        Check(Not ExperimentalFiles.SameXml(Encoding.UTF8.GetBytes("<!-- before --><a/><?note before?>"), Encoding.UTF8.GetBytes("<!-- after --><a/><?note after?>")), "top-level comment and processing-instruction edits are preserved")
        Check(ExperimentalFiles.SameXml(Encoding.UTF8.GetBytes("<!-- keep --><a x='1' y='2'/><?note keep?>"), Encoding.UTF8.GetBytes("<!-- keep -->" & vbLf & "<a y='2' x='1' />" & vbLf & "<?note keep?>")), "whitespace and attribute order alone remain formatting")
        Check(Not ExperimentalFiles.SameXml(applied, Encoding.UTF8.GetBytes("<!DOCTYPE x [<!ENTITY a SYSTEM 'file:///x'>]><x>&a;</x>")), "XML entities are not loaded")
        Reject(Sub() ExperimentalFiles.Graphics(Encoding.UTF8.GetBytes("<hardware_settings_config/>")), "missing graphics fields rejected")

        Dim restrictions = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "fixtures", "hardware_settings_restrictions.xml"))
        Dim serial = ExperimentalFiles.Restrictions(restrictions)
        Check(SafeFiles.HashBytes(serial) = "ea5e19b5fedc1e160010a0ca5dabe2307feea59b321f5a23658736b4c4e58ec9", "restrictions match independently verified minimal transform")
        Reject(Sub() ExperimentalFiles.Restrictions(serial), "already patched or foreign restrictions are rejected")
        Dim ctx = Context(root, "experimental-recovery")
        Directory.CreateDirectory(Path.Combine(ctx.GameRoot, "system"))
        Directory.CreateDirectory(Path.Combine(ctx.PackageRoot, "payload"))
        Dim restrictionsPath = Path.Combine(ctx.GameRoot, "system", "hardware_settings_restrictions.xml")
        Dim proxyPath = Path.Combine(ctx.GameRoot, "d3d11.dll")
        File.WriteAllBytes(ctx.GraphicsPath, original)
        File.WriteAllBytes(restrictionsPath, restrictions)
        File.WriteAllText(Path.Combine(ctx.PackageRoot, "payload", "d3d11.dll"), "owned fixture payload")
        Dim changes = ExperimentalFiles.Changes(ctx)
        Dim transaction As New OwnedFileTransaction(ctx)
        transaction.Prepare(changes, experimental:=True, apply:=False)
        Check(transaction.Pending AndAlso File.ReadAllBytes(ctx.GraphicsPath).SequenceEqual(original) AndAlso File.ReadAllBytes(restrictionsPath).SequenceEqual(restrictions) AndAlso Not File.Exists(proxyPath), "staging journals all targets without deployment")
        Check(transaction.Recover() AndAlso File.ReadAllBytes(ctx.GraphicsPath).SequenceEqual(original), "interrupted staging is recoverable")
        transaction.Prepare(changes, experimental:=True, apply:=False)
        File.WriteAllBytes(ctx.GraphicsPath, formatted) ' Simulate the game rewriting the applied XML.
        File.WriteAllBytes(restrictionsPath, serial)
        File.WriteAllBytes(proxyPath, changes("proxy"))
        Check(transaction.Recover(), "three-target recovery accepts only equivalent game formatting")
        Check(File.ReadAllBytes(ctx.GraphicsPath).SequenceEqual(original) AndAlso File.ReadAllBytes(restrictionsPath).SequenceEqual(restrictions) AndAlso Not File.Exists(proxyPath), "all originals restored exactly")
        Check(Directory.GetFiles(transaction.Folder, "game-written-*.xml").Length = 1, "game serialization retained for inspection")

        transaction.Prepare(changes, experimental:=True, apply:=False)
        File.WriteAllBytes(ctx.GraphicsPath, Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(applied).Replace("extra=""keep""", "extra=""user-change""")))
        File.WriteAllBytes(restrictionsPath, serial) : File.WriteAllBytes(proxyPath, changes("proxy"))
        Reject(Sub() transaction.Recover(), "real user edits in the graphics file are reported as a conflict")
        Check(Not File.Exists(proxyPath) AndAlso File.ReadAllBytes(restrictionsPath).SequenceEqual(restrictions) AndAlso Encoding.UTF8.GetString(File.ReadAllBytes(ctx.GraphicsPath)).Contains("user-change") AndAlso transaction.Pending,
              "a graphics conflict leaves only the graphics file and the journal; the proxy and restrictions are restored")
        File.WriteAllBytes(ctx.GraphicsPath, original)
        Check(transaction.Recover(), "original settings plus remaining targets recover after conflict review")

        Dim custom = ExperimentalFiles.Changes(ctx, 1800, 1950)
        Check(ExperimentalFiles.AppliedGraphicsSize(custom("graphics")) = New Size(1800, 1950), "headset-relative scene dimensions reach the graphics XML")
        transaction.Prepare(custom, experimental:=True, apply:=False)
        File.WriteAllBytes(ctx.GraphicsPath, custom("graphics"))
        File.WriteAllBytes(restrictionsPath, serial) : File.WriteAllBytes(proxyPath, custom("proxy"))
        Check(transaction.Recover() AndAlso File.ReadAllBytes(ctx.GraphicsPath).SequenceEqual(original), "custom headset scene dimensions recover the exact original settings")
        Reject(Sub() ExperimentalFiles.Graphics(original, 4097, 2600), "unsafe dimensions rejected before staging")
        Dim unrelated = custom.ToDictionary(Function(pair) pair.Key, Function(pair) pair.Value)
        unrelated("graphics") = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(custom("graphics")).Replace("mixing=""original""", "mixing=""changed"""))
        Reject(Sub() transaction.Prepare(unrelated, experimental:=True, apply:=False), "variable dimensions do not allow unrelated elevated file edits")
        Check(Not transaction.Pending AndAlso File.ReadAllBytes(ctx.GraphicsPath).SequenceEqual(original), "invalid prepared changes leave no pending transaction or edits")

        File.WriteAllText(proxyPath, "foreign proxy")
        Reject(Sub() transaction.Prepare(changes, experimental:=True, apply:=False), "foreign proxy blocks staging before pending journal")
        Check(Not transaction.Pending AndAlso File.ReadAllText(proxyPath) = "foreign proxy", "foreign proxy survives unchanged")
        File.Delete(proxyPath)
        transaction.Prepare(changes, experimental:=True, apply:=False)
        Dim journal = SafeFiles.ReadJson(Of RecoveryJournal)(transaction.JournalPath)
        File.WriteAllText(Path.Combine(transaction.Folder, journal.Entries.First(Function(e) e.Target = "graphics").AppliedBackup), "damaged")
        Reject(Sub() transaction.Recover(), "damaged applied backup rejects semantic recovery")
        Check(File.ReadAllBytes(ctx.GraphicsPath).SequenceEqual(original), "damaged evidence cannot overwrite original")

        Dim one = Context(root, "lease-one"), two = Context(root, "lease-two")
        Using first = InteractiveSession.AcquireLease(one)
            Reject(Sub() InteractiveSession.AcquireLease(two).Dispose(), "different installations cannot own shared Documents session together")
        End Using
        Using second = InteractiveSession.AcquireLease(two)
            Check(True, "closed session releases the cross-install lease")
        End Using
        Dim preferences As New LauncherSettings()
        SafeFiles.SaveJson(Path.Combine(ctx.PackageRoot, "runtime-capabilities.json"), New With {.schema = 1, .supportedExeSha256 = InstallContext.SupportedHash, .experimentalInteractive = True, .stereoVerified = False, .runtimes = {"vdxr", "steamvr"}})
        Reject(Sub() RuntimeCapabilities.RequireExperimental(ctx, preferences), "package capability does not imply user experimental opt-in")
        preferences.ExperimentalVrEnabled = True
        RuntimeCapabilities.RequireExperimental(ctx, preferences)
        Check(True, "explicit VDXR opt-in and matching package enable experimental path")
        Dim capabilityPath = Path.Combine(ctx.PackageRoot, "runtime-capabilities.json")
        Dim capabilityBytes = File.ReadAllBytes(capabilityPath)
        Dim utf8Bom As Byte() = {&HEF, &HBB, &HBF}
        SafeFiles.WriteAtomic(capabilityPath, utf8Bom.Concat(capabilityBytes).ToArray())
        Dim bomHash = SafeFiles.Hash(capabilityPath)
        RuntimeCapabilities.RequireExperimental(ctx, preferences)
        Check(RuntimeCapabilities.ExperimentalReason(ctx, preferences) = "", "PowerShell UTF-8 BOM does not block experimental VR")
        Check(SafeFiles.Hash(capabilityPath) = bomHash, "reading BOM JSON preserves its original bytes")
        For Each malformed In {New Byte() {&HEF, &HBB}, utf8Bom.Concat(utf8Bom).Concat(capabilityBytes).ToArray(), New Byte() {32}.Concat(utf8Bom).Concat(capabilityBytes).ToArray()}
            SafeFiles.WriteAtomic(capabilityPath, malformed)
            Reject(Sub() RuntimeCapabilities.Read(ctx), "malformed or misplaced BOM remains invalid JSON")
        Next
        SafeFiles.WriteAtomic(capabilityPath, utf8Bom.Concat(capabilityBytes).ToArray())
        preferences.RuntimeMode = "steamvr"
        Check(RuntimeCapabilities.ExperimentalReason(ctx, preferences).Contains("orientation"), "SteamVR interactive has a clear orientation-validation gate")
        Reject(Sub() RuntimeCapabilities.RequireExperimental(ctx, preferences), "SteamVR cannot bypass gate through edited preferences")
        Dim headset As New HeadsetResolution("vdxr", "Test headset", 2400, 2600)
        Dim runtimeInfo As New RuntimeInfo With {.Mode = "vdxr", .ManifestPath = "C:\runtime\virtualdesktop-openxr-32.json"}
        preferences.VrRenderScale = 75
        Dim start = InteractiveSession.StartInfo(one, runtimeInfo, Path.Combine(one.UserRoot, "logs", "fixture"), preferences, headset)
        Check(start.Environment("DIRT3VR_VR_RENDER_SCALE") = "75", "selected VR render scale reaches the VR child")
        Check(start.Environment("DIRT3VR_RECENTER_KEY") = "121 0", "default F10 recenter reaches the VR child")
        preferences.RecenterKey = 82 : preferences.RecenterModifiers = 5
        Check(InteractiveSession.StartInfo(one, runtimeInfo, Path.Combine(one.UserRoot, "logs", "fixture"), preferences, headset).Environment("DIRT3VR_RECENTER_KEY") = "82 5", "chosen recenter key and modifiers reach the VR child")
        preferences.RecenterKey = 9 : preferences.RecenterModifiers = 2
        Reject(Sub() InteractiveSession.StartInfo(one, runtimeInfo, Path.Combine(one.UserRoot, "logs", "fixture"), preferences, headset), "edited preferences cannot pass a reserved recenter key to the VR child")
        preferences.RecenterKey = RecenterShortcut.DefaultKey : preferences.RecenterModifiers = 0
        Check(start.Environment("DIRT3VR_PRESENTATION_TRACE") = If(preferences.DiagnosticLogging, "1", "0"), "presentation trace follows the logging preference")
        Dim logging = preferences.DiagnosticLogging
        preferences.DiagnosticLogging = False
        Check(InteractiveSession.StartInfo(one, runtimeInfo, Path.Combine(one.UserRoot, "logs", "fixture"), preferences, headset).Environment("DIRT3VR_PRESENTATION_TRACE") = "0", "presentation trace can be disabled")
        preferences.DiagnosticLogging = logging
        preferences.VrRenderScale = 301
        Reject(Sub() InteractiveSession.StartInfo(one, runtimeInfo, Path.Combine(one.UserRoot, "logs", "fixture"), preferences, headset), "invalid scale cannot reach a child process")
        preferences.VrRenderScale = 50
        Dim inheritedScale = Environment.GetEnvironmentVariable("DIRT3VR_VR_RENDER_SCALE")
        Try
            Environment.SetEnvironmentVariable("DIRT3VR_VR_RENDER_SCALE", "99")
            Check(Not Session.DesktopStartInfo(one).Environment.ContainsKey("DIRT3VR_VR_RENDER_SCALE"), "desktop removes inherited VR render scale")
            Check(InteractiveSession.StartInfo(one, runtimeInfo, Path.Combine(one.UserRoot, "logs", "fixture"), preferences, headset).Environment("DIRT3VR_VR_RENDER_SCALE") = "50", "selected VR scale overrides inherited environment")
        Finally
            Environment.SetEnvironmentVariable("DIRT3VR_VR_RENDER_SCALE", inheritedScale)
        End Try
        Check(Not start.UseShellExecute AndAlso start.ArgumentList.Count = 0 AndAlso start.Arguments = "", "interactive launch remains unelevated normal gameplay without benchmark arguments")
        Check(start.Environment("DIRT3VR_XR_INTERACTIVE") = "1" AndAlso start.Environment("DIRT3VR_XR_PROTOTYPE") = "1" AndAlso start.Environment("DIRT3VR_SCENE_DIAGNOSTICS") = "1", "interactive environment enables the exact native contract")
        Check(start.Environment("XR_RUNTIME_JSON") = runtimeInfo.ManifestPath AndAlso Path.IsPathFullyQualified(start.Environment("DIRT3VR_XR_STOP_FILE")), "runtime and stop path are explicit per-process values")
        Check(Not Session.DesktopStartInfo(one).Environment.ContainsKey("DIRT3VR_XR_INTERACTIVE"), "normal desktop launch has no interactive VR flag")
        Check(Not File.Exists(Path.Combine(one.GameRoot, "dirt3_game.exe")), "experimental fixtures never create or launch a game executable")
        Check(InteractiveSession.OutputStatus("XR prototype enabled").Contains("waiting"), "runtime initialization is not headset frame readiness")
        Check(InteractiveSession.OutputStatus("XR screen submitted=1 aspect=1280/720").Contains("Virtual screen"), "flat screen submission is distinguished from stereo")
        Check(InteractiveSession.OutputStatus("XR prototype main=123 submitted=1 total=1").Contains("frames submitted"), "native scene submission reports actual progress")
        Check(InteractiveSession.OutputStatus("XR screen submitted=300" & vbLf & "XR prototype main=123 submitted=1 total=1").Contains("Stereo scene"), "latest successful frame selects stereo status")
        Check(InteractiveSession.OutputStatus("XR prototype main=123 submitted=1 total=1" & vbLf & "XR screen submitted=300").Contains("Virtual screen"), "returning to screen clears stale stereo status")
        Check(InteractiveSession.OutputStatus("XR screen submitted=1" & vbLf & "XR prototype stopped reason=initialization").Contains("stopped"), "native failure overrides earlier successful frames")
        Check(InteractiveSession.OutputStatus("XR prototype stopped reason=initialization").Contains("reason=initialization"), "native failure reason is surfaced in session status")
        Check(InteractiveSession.OutputStatus("XR screen refused reason=guard").Contains("reason=guard"), "screen refusal is surfaced without stopping the game")
    End Sub

    Private Sub Reject(action As Action, message As String)
        Dim rejected = False
        Try
            action()
        Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is JsonException OrElse TypeOf ex Is ArgumentException
            rejected = True
        End Try
        Check(rejected, message)
    End Sub

    Private Function Context(root As String, name As String) As InstallContext
        Dim game = Path.Combine(root, name)
        Directory.CreateDirectory(game)
        Return New InstallContext(game, Path.Combine(root, "users"), Path.Combine(game, "hardware_settings_config.xml"), root)
    End Function

    Private Sub TestHeadsetResolution(root As String)
        Dim report = JsonSerializer.SerializeToElement(New With {.success = True, .stereo = False, .sessionCreated = False, .runtimeName = "VirtualDesktopXR", .systemName = "Test headset", .views = {
            New With {.width = 2400, .height = 2600, .maxWidth = 4096, .maxHeight = 4096},
            New With {.width = 2400, .height = 2600, .maxWidth = 4096, .maxHeight = 4096}}})
        Dim headset = HeadsetResolution.FromReport(report, "vdxr")
        For Each scale In {50, 75, 100, 137, 150}
            Dim size = headset.SceneSize(scale)
            Check(size = New Size(2400 * scale \ 100, 2600 * scale \ 100), "scene dimensions use the connected headset baseline")
            Check(headset.Summary(scale).Contains("Output: 2400 × 2600") AndAlso headset.Summary(scale).Contains($"Render: {size.Width} × {size.Height}"), "output dimensions stay fixed as scene scale changes: " & headset.Summary(scale))
        Next
        Check(headset.MaximumScale = 157 AndAlso headset.SceneSize(157) = New Size(3768, 4082), "reported headset-specific maximum is usable")
        Reject(Sub() headset.SceneSize(158), "next percentage above the limit is refused")
        Check(headset.Summary(300).Contains("unsupported"), "unsupported slider requests show a clear explanation")
        For Each invalid In {49, 301, Integer.MaxValue}
            Reject(Sub() headset.SceneSize(invalid), "scale limits checked before arithmetic")
        Next
        For Each reportText As String In {"{}", "null", report.GetRawText().Replace("VirtualDesktopXR", "UnexpectedRuntime"), report.GetRawText().Replace("Test headset", ""),
            report.GetRawText().Replace("2400", "0"), report.GetRawText().Replace("2400", "4097"), report.GetRawText().Replace("2600", "2147483647"),
            report.GetRawText().Replace("4096", "1000"), report.GetRawText().Replace("false", "true")}
            Using document = JsonDocument.Parse(reportText)
                Reject(Sub() HeadsetResolution.FromReport(document.RootElement, "vdxr"), "malformed or incompatible headset report refused")
            End Using
        Next
        Dim mismatched = System.Text.Json.Nodes.JsonNode.Parse(report.GetRawText())
        mismatched("views")(1)("width") = System.Text.Json.Nodes.JsonValue.Create(2300)
        Using document = JsonDocument.Parse(mismatched.ToJsonString())
            Reject(Sub() HeadsetResolution.FromReport(document.RootElement, "vdxr"), "asymmetric recommended eye dimensions refused")
        End Using
        Reject(Sub() HeadsetResolution.FromReport(report, "steamvr"), "a VDXR result cannot be reused for SteamVR")
        Dim steam = JsonDocument.Parse(report.GetRawText().Replace("VirtualDesktopXR", "SteamVR/OpenXR"))
        Using steam
            Check(HeadsetResolution.FromReport(steam.RootElement, "steamvr").Width = 2400, "selected SteamVR headset query remains supported")
        End Using
        For Each oldScale In {25, 50, 75, 100}
            Dim context = Program.Context(root, "legacy-output-" & oldScale)
            SafeFiles.SaveJson(context.PreferencesPath, New With {.Version = 1, .HeadsetScale = oldScale, .ExperimentalVrEnabled = True})
            Dim hash = SafeFiles.Hash(context.PreferencesPath)
            Dim migrated = LauncherSettings.Load(context)
            Check(migrated.VrRenderScale = 100 AndAlso migrated.ExperimentalVrEnabled, "legacy output scale is not reinterpreted as scene scale")
            Check(SafeFiles.Hash(context.PreferencesPath) = hash, "migration does not rewrite unsaved preferences")
        Next
        Dim install = Context(root, "headset-environment"), settings As New LauncherSettings With {.VrRenderScale = 150}
        Dim runtime As New RuntimeInfo With {.Mode = "vdxr", .ManifestPath = "C:\runtime\virtualdesktop-openxr-32.json"}
        Dim start = InteractiveSession.StartInfo(install, runtime, install.UserRoot, settings, headset)
        Check(start.Environment("DIRT3VR_HEADSET_WIDTH") = "2400" AndAlso start.Environment("DIRT3VR_HEADSET_HEIGHT") = "2600" AndAlso start.Environment("DIRT3VR_VR_RENDER_SCALE") = "150", "one plan supplies baseline and scene scale to native runtime")
        Check(Not start.Environment.ContainsKey("DIRT3VR_HEADSET_SCALE_PERCENT"), "obsolete output scale is never sent to the game")
        settings.VrRenderScale = 300
        Reject(Sub() InteractiveSession.StartInfo(install, runtime, install.UserRoot, settings, headset), "unsafe scene request cannot reach the child")
        settings.VrRenderScale = 100
        Reject(Sub() InteractiveSession.StartInfo(install, runtime, install.UserRoot, settings, Nothing), "missing headset cannot fall back to fixed dimensions")
        Reject(Sub() InteractiveSession.StartInfo(install, runtime, install.UserRoot, settings, New HeadsetResolution("steamvr", "Other", 2400, 2600)), "launch rejects a result from another runtime")
    End Sub

    Private Sub TestSettings(root As String)
        Dim one = Context(root, "game-one"), two = Context(root, "game-two")
        Dim settings = LauncherSettings.Load(one)
        Check(settings.ModernInterface AndAlso settings.RuntimeMode = "vdxr", "new install defaults Modern and explicit VDXR")
        Check(settings.VrRenderScale = 100, "new install uses recommended headset resolution at 100 percent")
        settings.ModernInterface = False : settings.RuntimeMode = "steamvr"
        settings.SteamRuntimeManifest = "C:\runtime folder\steamxr_win32.json"
        settings.ExperimentalVrEnabled = True
        settings.VrRenderScale = 75
        settings.Save(one)
        Check(Not LauncherSettings.Load(one).ModernInterface AndAlso LauncherSettings.Load(one).RuntimeMode = "steamvr", "settings round trip")
        Check(LauncherSettings.Load(one).ExperimentalVrEnabled AndAlso Not LauncherSettings.Load(two).ExperimentalVrEnabled, "experimental opt-in persists only for its installation")
        Check(LauncherSettings.Load(two).ModernInterface AndAlso one.UserRoot <> two.UserRoot, "install preferences are isolated")
        Check(LauncherSettings.Load(one).VrRenderScale = 75 AndAlso LauncherSettings.Load(two).VrRenderScale = 100, "headset VR render scale persists independently per install")
        For Each invalidScale In {49, 301, Integer.MaxValue}
            Dim invalid As New LauncherSettings With {.VrRenderScale = invalidScale}
            Reject(Sub() invalid.Save(two), "out-of-range VR render scale rejected")
        Next
        Check(Not File.Exists(two.PreferencesPath), "invalid VR render scale writes no preferences")
        Dim legacy = Context(root, "legacy-openxr")
        SafeFiles.SaveJson(legacy.PreferencesPath, New With {.Version = 1, .RuntimeMode = "openxr", .SteamRuntimeManifest = "C:\Steam\steamxr_win32.json", .ModernInterface = False})
        Dim legacyHash = SafeFiles.Hash(legacy.PreferencesPath)
        Dim migrated = LauncherSettings.Load(legacy)
        Check(migrated.VrRenderScale = 100, "legacy settings start the new headset-relative scale at 100 percent")
        Check(migrated.RuntimeMode = "vdxr" AndAlso Not migrated.ModernInterface AndAlso migrated.SteamRuntimeManifest = "C:\Steam\steamxr_win32.json", "legacy openxr migrates without losing other preferences")
        Check(SafeFiles.Hash(legacy.PreferencesPath) = legacyHash, "loading legacy settings does not write them")
        migrated.VdxrRuntimeManifest = "D:\Custom Streamer\OpenXR\virtualdesktop-openxr-32.json"
        migrated.Save(legacy)
        Check(LauncherSettings.Load(legacy).VdxrRuntimeManifest = migrated.VdxrRuntimeManifest AndAlso LauncherSettings.Load(legacy).RuntimeMode = "vdxr", "migration and custom VDXR path persist on save")
        Check(New InstallContext(one.GameRoot.ToUpperInvariant(), Path.Combine(root, "users")).Id = one.Id, "install identity is case insensitive")
        File.WriteAllText(one.PreferencesPath, "{broken")
        Dim hash = SafeFiles.Hash(one.PreferencesPath)
        Reject(Sub() LauncherSettings.Load(one), "corrupt settings rejected")
        Check(SafeFiles.Hash(one.PreferencesPath) = hash, "corrupt settings preserved")
        settings.Version = 99
        Reject(Sub() settings.Save(two), "future settings schema not overwritten")
        Check(Not File.Exists(two.PreferencesPath), "invalid settings leave no file")
    End Sub

    Private Sub TestWindowPreferences(root As String)
        Dim ctx = Context(root, "window-preferences")
        Dim fallback As New Size(900, 760)
        Check(WindowPreferences.Load(ctx).Fit(New Size(1920, 1080), 96, fallback) = fallback, "missing window preferences retain default size")
        For Each dpi In {96, 144, 192}
            Dim normal As New Size(1000 * dpi \ 96, 800 * dpi \ 96)
            Dim saved = WindowPreferences.Capture(normal, dpi, True)
            saved.Save(ctx)
            Dim loaded = WindowPreferences.Load(ctx)
            Check(loaded.Maximized AndAlso loaded.Fit(New Size(4000, 3000), dpi, fallback) = normal, "logical window size and maximized state round trip at each DPI")
            Dim fitted = loaded.Fit(New Size(500, 400), dpi, fallback)
            Check(fitted = New Size(500, 400), "window fits a work area smaller than its normal minimum")
        Next
        Dim file = Path.Combine(ctx.UserRoot, "window-preferences.json")
        For Each damaged In {"{broken", "null", "{""Version"":99,""Width"":900,""Height"":760}", "{""Width"":2147483647,""Height"":760}"}
            IO.File.WriteAllText(file, damaged)
            Dim hash = SafeFiles.Hash(file)
            Check(WindowPreferences.Load(ctx).Fit(New Size(1920, 1080), 96, fallback) = fallback, "damaged window data cannot prevent usable startup")
            Check(SafeFiles.Hash(file) = hash, "reading damaged window data preserves it")
        Next
        Check(Not IO.File.Exists(ctx.PreferencesPath), "window persistence never creates game preferences")
    End Sub

    Private Sub TestRecovery(root As String)
        Dim ctx = Context(root, "recover-roundtrip")
        Dim original = Encoding.UTF8.GetBytes("<graphics original='true' />")
        Dim replacement = Encoding.UTF8.GetBytes("<graphics vr='true' />")
        Dim proxy = Encoding.UTF8.GetBytes("owned test proxy")
        File.WriteAllBytes(ctx.GraphicsPath, original)
        Dim transaction As New OwnedFileTransaction(ctx)
        Check(Not transaction.Recover(), "empty recovery does not modify files")
        transaction.Prepare(New Dictionary(Of String, Byte()) From {{"graphics", replacement}, {"proxy", proxy}})
        Check(transaction.Pending AndAlso File.ReadAllBytes(ctx.GraphicsPath).SequenceEqual(replacement), "preparation journals applied changes")
        Check(transaction.Recover(), "restoration succeeds")
        Check(File.ReadAllBytes(ctx.GraphicsPath).SequenceEqual(original) AndAlso Not File.Exists(Path.Combine(ctx.GameRoot, "d3d11.dll")), "original graphics restored and owned proxy removed")
        Check(Not transaction.Pending AndAlso Not transaction.Recover(), "recovery is idempotent")

        Dim foreign = Context(root, "foreign-proxy")
        Dim foreignPath = Path.Combine(foreign.GameRoot, "d3d11.dll")
        File.WriteAllText(foreignPath, "another mod")
        Dim foreignTransaction As New OwnedFileTransaction(foreign)
        Reject(Sub() foreignTransaction.Prepare(New Dictionary(Of String, Byte()) From {{"proxy", proxy}}), "foreign proxy rejected")
        Check(File.ReadAllText(foreignPath) = "another mod", "foreign proxy bytes preserved")

        Dim conflict = Context(root, "recover-conflict")
        File.WriteAllBytes(conflict.GraphicsPath, original)
        Dim conflicted As New OwnedFileTransaction(conflict)
        conflicted.Prepare(New Dictionary(Of String, Byte()) From {{"proxy", proxy}, {"graphics", replacement}})
        File.WriteAllText(conflict.GraphicsPath, "external edit")
        Reject(Sub() conflicted.Recover(), "external edits are reported as a recovery conflict")
        Check(File.ReadAllText(conflict.GraphicsPath) = "external edit" AndAlso Not File.Exists(Path.Combine(conflict.GameRoot, "d3d11.dll")), "an edited file is left as it is while the unchanged proxy is still removed")
        Check(conflicted.Pending AndAlso Directory.GetFiles(conflicted.Folder, "*.bin").Length = 1, "conflict retains original backup and journal")
        File.WriteAllBytes(conflict.GraphicsPath, original)
        Check(conflicted.Recover(), "already restored target permits remaining restoration")

        Dim missing = Context(root, "missing-backup")
        File.WriteAllBytes(missing.GraphicsPath, original)
        Dim broken As New OwnedFileTransaction(missing)
        broken.Prepare(New Dictionary(Of String, Byte()) From {{"graphics", replacement}})
        Dim journal = SafeFiles.ReadJson(Of RecoveryJournal)(broken.JournalPath)
        File.WriteAllText(Path.Combine(broken.Folder, journal.Entries(0).Backup), "changed backup")
        Reject(Sub() broken.Recover(), "corrupted backup rejected")
        Check(File.ReadAllBytes(missing.GraphicsPath).SequenceEqual(replacement) AndAlso broken.Pending, "backup damage preserves target and journal")
        journal.Entries(0).Target = "../../outside"
        SafeFiles.SaveJson(broken.JournalPath, journal)
        Reject(Sub() broken.Recover(), "journal path traversal rejected")
        journal.Entries(0).Target = "graphics" : journal.InstallId = "another install"
        SafeFiles.SaveJson(broken.JournalPath, journal)
        Reject(Sub() broken.Recover(), "cross-install journal rejected")
        journal.InstallId = missing.Id : journal.Entries(0).Backup = Nothing
        SafeFiles.SaveJson(broken.JournalPath, journal)
        Reject(Sub() broken.Recover(), "null backup field rejected clearly")
        journal.Entries(0).Backup = Guid.NewGuid().ToString("N") & ".bin" : journal.Entries(0).AppliedHash = Nothing
        SafeFiles.SaveJson(broken.JournalPath, journal)
        Reject(Sub() broken.Recover(), "null hash field rejected clearly")
        Reject(Sub() RecoveryWorker.ValidateGraphicsPath(Path.Combine(root, "arbitrary.xml")), "elevated worker rejects arbitrary target")
        RecoveryWorker.ValidateGraphicsPath(Path.Combine(root, "Documents\My Games\DiRT3\hardwaresettings\hardware_settings_config.xml"))
        Check(True, "elevated worker accepts exact DiRT 3 settings suffix")

        ' Players make game settings read-only on purpose. Replacing keeps the flag,
        ' sessions no longer refuse such files, and recovery puts the recorded flag back.
        Dim lockedFile = Path.Combine(root, "locked-write.json")
        File.WriteAllText(lockedFile, "old") : File.SetAttributes(lockedFile, FileAttributes.ReadOnly Or FileAttributes.Hidden)
        SafeFiles.WriteAtomic(lockedFile, Encoding.UTF8.GetBytes("new"))
        Check(File.ReadAllText(lockedFile) = "new" AndAlso SafeFiles.IsReadOnly(lockedFile) AndAlso (File.GetAttributes(lockedFile) And FileAttributes.Hidden) <> 0, "atomic replacement of a read-only file keeps its attributes")
        Check(Directory.GetFiles(root, "locked-write.json.*.tmp").Length = 0, "read-only replacement leaves no temporary file")
        SafeFiles.SetReadOnly(lockedFile, False)
        Check(Not SafeFiles.IsReadOnly(lockedFile), "read-only flag can be cleared")

        Dim locked = Context(root, "recover-read-only")
        File.WriteAllBytes(locked.GraphicsPath, original) : SafeFiles.SetReadOnly(locked.GraphicsPath, True)
        Dim lockedTransaction As New OwnedFileTransaction(locked)
        lockedTransaction.Prepare(New Dictionary(Of String, Byte()) From {{"graphics", replacement}, {"proxy", proxy}})
        Dim lockedJournal = SafeFiles.ReadJson(Of RecoveryJournal)(lockedTransaction.JournalPath)
        Check(lockedJournal.Entries.Single(Function(e) e.Target = "graphics").OriginalReadOnly AndAlso Not lockedJournal.Entries.Single(Function(e) e.Target = "proxy").OriginalReadOnly, "journal records which originals were read-only")
        Check(File.ReadAllBytes(locked.GraphicsPath).SequenceEqual(replacement) AndAlso SafeFiles.IsReadOnly(locked.GraphicsPath), "a read-only game file is applied and stays read-only during the session")
        Check(lockedTransaction.Recover() AndAlso File.ReadAllBytes(locked.GraphicsPath).SequenceEqual(original) AndAlso SafeFiles.IsReadOnly(locked.GraphicsPath), "recovery restores a read-only original with its flag")
        Check(Not File.Exists(Path.Combine(locked.GameRoot, "d3d11.dll")), "owned proxy removed beside a read-only original")

        Dim unlocked = Context(root, "recover-flag-lost")
        File.WriteAllBytes(unlocked.GraphicsPath, original) : SafeFiles.SetReadOnly(unlocked.GraphicsPath, True)
        Dim unlockedTransaction As New OwnedFileTransaction(unlocked)
        unlockedTransaction.Prepare(New Dictionary(Of String, Byte()) From {{"graphics", replacement}})
        SafeFiles.SetReadOnly(unlocked.GraphicsPath, False) ' e.g. the game or another tool rewrote the file during play
        Check(unlockedTransaction.Recover() AndAlso File.ReadAllBytes(unlocked.GraphicsPath).SequenceEqual(original) AndAlso SafeFiles.IsReadOnly(unlocked.GraphicsPath), "recovery puts back a read-only flag lost during the session")

        Dim legacy = Context(root, "recover-legacy-flag")
        File.WriteAllBytes(legacy.GraphicsPath, original)
        Dim legacyTransaction As New OwnedFileTransaction(legacy)
        legacyTransaction.Prepare(New Dictionary(Of String, Byte()) From {{"graphics", replacement}})
        Dim legacyJson = Nodes.JsonNode.Parse(File.ReadAllText(legacyTransaction.JournalPath))
        For Each legacyEntry In legacyJson("Entries").AsArray()
            legacyEntry.AsObject().Remove("OriginalReadOnly")
        Next
        File.WriteAllText(legacyTransaction.JournalPath, legacyJson.ToJsonString())
        Check(Not File.ReadAllText(legacyTransaction.JournalPath).Contains("OriginalReadOnly"), "legacy journal fixture has no read-only field")
        Check(legacyTransaction.Recover() AndAlso File.ReadAllBytes(legacy.GraphicsPath).SequenceEqual(original) AndAlso Not SafeFiles.IsReadOnly(legacy.GraphicsPath), "journals written before read-only support still recover")

        Dim partialContext = Context(root, "partial-preparation")
        File.WriteAllBytes(partialContext.GraphicsPath, original)
        Dim partialTransaction As New OwnedFileTransaction(partialContext)
        partialTransaction.Prepare(New Dictionary(Of String, Byte()) From {{"graphics", replacement}, {"proxy", proxy}})
        File.Delete(Path.Combine(partialContext.GameRoot, "d3d11.dll")) ' Simulate crash before the second target write.
        Check(partialTransaction.Recover() AndAlso File.ReadAllBytes(partialContext.GraphicsPath).SequenceEqual(original), "partial preparation restores only applied targets")
    End Sub

    Private Sub TestRuntime(root As String)
        Dim x86 = Path.Combine(root, "x86.dll"), x64 = Path.Combine(root, "x64.dll")
        For Each pair In New Dictionary(Of String, UShort) From {{x86, &H14CUS}, {x64, &H8664US}}
            Using stream = File.Create(pair.Key), writer As New BinaryWriter(stream)
                stream.SetLength(128) : writer.Write(CUShort(&H5A4D)) : stream.Position = &H3C
                writer.Write(64) : stream.Position = 64 : writer.Write(&H4550UI) : writer.Write(pair.Value)
            End Using
        Next
        Dim manifest = Path.Combine(root, "test-runtime.json")
        SafeFiles.SaveJson(manifest, New With {.runtime = New With {.name = "Test runtime", .library_path = "x86.dll"}})
        Check(VrRuntime.ReadManifest("vdxr", manifest).Name = "Test runtime", "relative x86 runtime library accepted")
        SafeFiles.SaveJson(manifest, New With {.runtime = New With {.library_path = "x64.dll"}})
        Reject(Sub() VrRuntime.ReadManifest("vdxr", manifest), "x64 runtime rejected for x86 game")
        Reject(Sub() VrRuntime.ReadManifest("vdxr", "relative.json"), "relative manifest rejected")
        Dim explicitVdxr = Path.Combine(root, "virtualdesktop-openxr-32.json")
        Dim explicitSteam = Path.Combine(root, "steamxr_win32.json")
        SafeFiles.SaveJson(explicitVdxr, New With {.runtime = New With {.library_path = "x86.dll"}})
        SafeFiles.SaveJson(explicitSteam, New With {.runtime = New With {.library_path = "x86.dll"}})
        Check(VrRuntime.Resolve("vdxr", explicitSteam, explicitVdxr).ManifestPath = explicitVdxr, "VDXR resolves its own explicit manifest without system registration")
        Check(VrRuntime.Resolve("openxr", explicitSteam, explicitVdxr).Mode = "vdxr", "old command-line choice aliases explicit VDXR")
        Check(VrRuntime.Resolve("steamvr", explicitSteam, explicitVdxr).ManifestPath = explicitSteam, "SteamVR resolves independently of VDXR")
        Reject(Sub() VrRuntime.Resolve("vdxr", explicitSteam, Path.Combine(root, "missing", "virtualdesktop-openxr-32.json")), "missing VDXR reports failure without falling back to system runtime")
        Reject(Sub() VrRuntime.Resolve("vdxr", explicitSteam, explicitSteam), "VDXR rejects SteamVR manifest")
        Reject(Sub() VrRuntime.Resolve("steamvr", explicitVdxr, explicitVdxr), "SteamVR rejects VDXR manifest")
        Check(Discovery.VdxrRuntime().EndsWith("Virtual Desktop Streamer\OpenXR\virtualdesktop-openxr-32.json", StringComparison.OrdinalIgnoreCase), "VDXR discovery targets installed 32-bit runtime")
        Dim output = "{""success"":true,""runtimeName"":""test"",""stereo"":false}"
        Check(VrRuntime.ValidateProbeOutput(output, 0, "").GetProperty("runtimeName").GetString() = "test", "successful probe report accepted without implying VR")
        Reject(Sub() VrRuntime.ValidateProbeOutput(output, 1, "failed"), "nonzero native exit rejected")
        Reject(Sub() VrRuntime.ValidateProbeOutput("{""success"":false,""error"":""no headset"",""stereo"":false}", 0, ""), "failure report rejected even with zero exit")
        Reject(Sub() VrRuntime.ValidateProbeOutput("{""success"":true,""stereo"":true}", 0, ""), "unexpected stereo flag rejected")
        Reject(Sub() VrRuntime.ValidateProbeOutput("not JSON", 0, ""), "malformed probe output rejected")
        Dim ctx = Context(root, "capability")
        SafeFiles.SaveJson(Path.Combine(ctx.PackageRoot, "runtime-capabilities.json"), New With {.schema = 1, .supportedExeSha256 = InstallContext.SupportedHash, .stereoVerified = True, .runtimes = {"vdxr", "steamvr"}})
        Check(RuntimeCapabilities.Read(ctx).StereoVerified, "capability fixture read")
        Reject(Sub() RuntimeCapabilities.RequireStereo(ctx), "edited capability cannot enable unimplemented VR")
        Directory.CreateDirectory(Path.Combine(ctx.PackageRoot, "payload"))
        File.WriteAllText(Path.Combine(ctx.PackageRoot, "payload", "DiRT3VR.RuntimeProbe.exe"), "never executed in tests")
        Dim globalRuntime = Environment.GetEnvironmentVariable("XR_RUNTIME_JSON")
        Dim probeStart = VrRuntime.ProbeStartInfo(ctx, VrRuntime.Resolve("vdxr", explicitSteam, explicitVdxr))
        Check(probeStart.Environment("XR_RUNTIME_JSON") = explicitVdxr AndAlso probeStart.ArgumentList.SequenceEqual({"--runtime-json", explicitVdxr}), "probe receives the same selected runtime in arguments and its own environment")
        Check(Environment.GetEnvironmentVariable("XR_RUNTIME_JSON") = globalRuntime, "runtime choice does not alter launcher or global environment")
    End Sub

    Private Sub TestLaunch(root As String)
        Dim ctx = Context(root, "launch-info")
        Dim start = Session.DesktopStartInfo(ctx)
        Check(start.FileName = Path.Combine(ctx.GameRoot, "dirt3_game.exe") AndAlso start.WorkingDirectory = ctx.GameRoot, "desktop targets exact selected installation")
        Check(start.Environment("SteamAppId") = "321040" AndAlso Not start.Environment.ContainsKey("XR_RUNTIME_JSON"), "desktop launch pins correct app without forcing XR")
        Reject(Sub() ctx.ValidateGame(), "missing game rejected")
        File.WriteAllText(start.FileName, "unsupported executable")
        Reject(Sub() ctx.ValidateGame(), "unsupported executable rejected")
        Check(File.ReadAllText(start.FileName) = "unsupported executable", "identity check does not modify executable")
        Session.WriteLaunchStatus(ctx, "failed", "early exit", 42, DateTime.UtcNow, -1073741819, "InitialLoading")
        Using log = JsonDocument.Parse(File.ReadAllText(Path.Combine(ctx.UserRoot, "logs", "last-launch.json")))
            Check(log.RootElement.GetProperty("stage").GetString() = "failed" AndAlso log.RootElement.GetProperty("exitCode").GetInt32() = -1073741819,
                  "startup failure and exit code are recorded even with default logging preferences")
        End Using
    End Sub

    Private Sub TestAppearance(root As String, Optional package As String = Nothing)
        Application.SetCompatibleTextRenderingDefault(False)
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2)
        Application.SetColorMode(SystemColorMode.System)
        Application.EnableVisualStyles()
        DesktopTests.RunWindows(AddressOf Check)
        StartupTests.RunWindowLifecycle(root, AddressOf Check)
        ProfileTests.RunUi(root, AddressOf Check)
        RecenterTests.RunUi(root, AddressOf Check)
        DrivingInputTests.RunUi(root, AddressOf Check)
        UpdateTests.RunUi(root, AddressOf Check)
        VrLayerTests.RunUi(root, AddressOf Check)
        RecenterDeviceTests.RunUi(root, AddressOf Check)
        BackgroundTests.RunUi(root, AddressOf Check)
        RecoveryConflictTests.RunUi(root, AddressOf Check)
        DefaultsTests.RunUi(root, AddressOf Check)
        HelpTests.RunUi(root, AddressOf Check)
        StatusTextTests.RunUi(root, AddressOf Check)
        LastVrSessionTests.RunUi(root, AddressOf Check)
        RecenterTiltTests.RunUi(root, AddressOf Check)
        GraphicsQualityTests.RunUi(root, AddressOf Check)
        DirectPracticeTests.RunUi(root, AddressOf Check)
        SimpleViewTests.RunUi(root, AddressOf Check)
        NewFeaturesTests.RunUi(root, AddressOf Check)
        SeatTests.RunUi(root, AddressOf Check)
        DrivingControlsTests.RunUi(root, AddressOf Check)
        WheelGuardTests.Run(root, AddressOf Check)
        InstallerTests.RunUi(AddressOf Check)
        SectionCollapseTests.Run(root, AddressOf Check)
        Check(LauncherAppearance.AccentFor(True) = Color.FromArgb(255, 213, 74), "dark accent is yellow")
        Check(LauncherAppearance.AccentFor(False) = Color.FromArgb(122, 89, 0), "light accent has readable contrast")
        Dim install = Context(root, "ui")
        If package IsNot Nothing Then install = New InstallContext(install.GameRoot, Path.Combine(root, "users"), install.GraphicsPath, package)
        Using form As New MainForm(install)
            form.ShowInTaskbar = False : form.StartPosition = FormStartPosition.Manual : form.Location = New Point(-30000, -30000)
            form.Show() : Application.DoEvents()
            Dim tabs = DirectCast(form.Controls.Find("LauncherTabs", True).Single(), TabControl)
            Dim modern = DirectCast(form.Controls.Find("ModernInterface", True).Single(), CheckBox)
            Dim runtimePath = DirectCast(form.Controls.Find("RuntimePath", True).Single(), TextBox)
            Dim vdxrPath = DirectCast(form.Controls.Find("VdxrRuntimePath", True).Single(), TextBox)
            Dim choice = DirectCast(form.Controls.Find("RuntimeChoice", True).Single(), ComboBox)
            Dim outputScale = DirectCast(form.Controls.Find("VrRenderScale", True).Single(), ValueSlider)
            Dim displayMode = DirectCast(form.Controls.Find("DesktopDisplayMode", True).Single(), ComboBox)
            Dim desktopSize = DirectCast(form.Controls.Find("DesktopResolution", True).Single(), ComboBox)
            Dim desktopSync = DirectCast(form.Controls.Find("DesktopVSync", True).Single(), ComboBox)
            Dim skipMovies = DirectCast(form.Controls.Find("SkipStartupMovies", True).Single(), CheckBox)
            Check(Not skipMovies.Checked, "startup movie skipping is opt-in")
            skipMovies.Checked = True
            Check(form.CurrentSettings().SkipStartupMovies, "startup choice reaches both launch modes through settings")
            Check(Not DesktopGraphics.HasOverrides(form.CurrentSettings()), "UI starts with game-controlled desktop defaults")
            desktopSize.SelectedIndex = 1 : desktopSync.SelectedIndex = 2
            Dim selectedDesktopSize = DirectCast(desktopSize.SelectedItem, DesktopResolutionChoice).Size
            displayMode.SelectedIndex = 3
            Check(Not desktopSize.Enabled AndAlso form.CurrentSettings().DesktopWidth = selectedDesktopSize.Width, "borderless disables but retains the resolution choice")
            displayMode.SelectedIndex = 2
            Check(desktopSize.Enabled AndAlso form.CurrentSettings().DesktopDisplayMode = "fullscreen" AndAlso form.CurrentSettings().DesktopVSync = "on", "desktop controls reach the launch settings snapshot")
            Check(form.Text.Contains(GetType(MainForm).Assembly.GetName().Version.ToString(3)), "launcher title identifies its assembly build")
            Check(outputScale.Value = 100, "single VR slider defaults to the detected recommendation")
            Check(form.Controls.Find("HeadsetScale", True).Length = 0, "obsolete output slider is absent")
            outputScale.Value = 75
            Check(form.CurrentSettings().VrRenderScale = 75, "slider edits reach the settings snapshot")
            Const privateFlags As Reflection.BindingFlags = Reflection.BindingFlags.Instance Or Reflection.BindingFlags.NonPublic
            Dim detected = GetType(MainForm).GetField("headsetResolution", privateFlags)
            Dim refreshLabel = GetType(MainForm).GetMethod("UpdateResolutionLabels", privateFlags)
            detected.SetValue(form, New HeadsetResolution("vdxr", "Test headset", 2400, 2600))
            refreshLabel.Invoke(form, Nothing)
            Dim summary = DirectCast(form.Controls.Find("VrResolutionSummary", True).Single(), Label)
            outputScale.Value = 150
            Check(summary.Text.Contains("Render: 3600 × 3900") AndAlso summary.Text.Contains("Output: 2400 × 2600"), "one slider updates scene dimensions without changing output")
            outputScale.Value = 75
            Dim notice = form.Controls.Find("Notice", True).Single()
            notice.Text = "DiRT 3 exited before its game window appeared (exit code 0xC0000005). Open logs for details."
            Check(choice.Items.Cast(Of String)().SequenceEqual({"VDXR", "SteamVR"}), "runtime choices are explicit")
            Check(tabs.TabPages.Cast(Of TabPage)().Select(Function(p) p.Text).SequenceEqual({"Launcher", "Graphics", "Controls", "Settings", "Advanced"}), "familiar tab order")
            runtimePath.Text = "Unsaved runtime"
            vdxrPath.Text = "Unsaved VDXR runtime"
            choice.SelectedIndex = 1
            Check(runtimePath.Enabled AndAlso Not vdxrPath.Enabled, "SteamVR selection enables only its manifest")
            choice.SelectedIndex = 0
            Check(vdxrPath.Enabled AndAlso Not runtimePath.Enabled, "VDXR selection enables only its manifest")
            Check(detected.GetValue(form) Is Nothing AndAlso summary.Text.Contains("refresh"), "runtime or manifest edits invalidate stale headset dimensions")
            detected.SetValue(form, New HeadsetResolution("vdxr", "Test headset", 2400, 2600))
            refreshLabel.Invoke(form, Nothing)
            For Each style In {False, True}
                modern.Checked = style
                For Each width In {560, 900, 1440}
                    ' Widths are logical units. A physical 560px request at 150%
                    ' is below the DPI-aware minimum and Windows clamps it.
                    form.ClientSize = New Size(Px(form, width), Px(form, 760))
                    For Each tab As TabPage In tabs.TabPages
                        tabs.SelectedTab = tab : Application.DoEvents()
                        Check(runtimePath.Text = "Unsaved runtime", "style and resize preserve unsaved settings")
                        Check(vdxrPath.Text = "Unsaved VDXR runtime", "style and resize preserve independent VDXR path")
                        Check(outputScale.Value = 75, "style and resize preserve unsaved VR render scale")
                        Check(form.CurrentSettings().SkipStartupMovies, "style and resize preserve unsaved startup choice")
                        Check(form.CurrentSettings().DesktopWidth = selectedDesktopSize.Width AndAlso form.CurrentSettings().DesktopHeight = selectedDesktopSize.Height AndAlso form.CurrentSettings().DesktopDisplayMode = "fullscreen" AndAlso form.CurrentSettings().DesktopVSync = "on", "style and resize preserve all unsaved desktop settings")
                        Check(Not tab.HorizontalScroll.Visible, $"no horizontal overflow: {style} {width} {tab.Text}")
                        For Each name In {"LaunchDesktop", "LaunchVR", "SaveSettings", "RestoreFiles", "OpenLogs"}
                            Dim button = form.Controls.Find(name, True).Single()
                            Dim contained = form.RectangleToScreen(form.ClientRectangle).Contains(button.RectangleToScreen(button.ClientRectangle))
                            If Not contained Then
                                Using bitmap As New Bitmap(form.Width, form.Height)
                                    form.DrawToBitmap(bitmap, New Rectangle(Point.Empty, bitmap.Size))
                                    bitmap.Save(Path.Combine(root, "layout-failure.png"))
                                End Using
                                Console.WriteLine("Layout artifact: " & root)
                            End If
                            Check(contained, $"footer remains visible: {name}, modern={style}, width={width}, page={tab.Text}, dpi={form.DeviceDpi}, client={form.ClientSize}, screen={form.RectangleToScreen(form.ClientRectangle)}, buttonScreen={button.RectangleToScreen(button.ClientRectangle)}, footer={button.Parent.Bounds}, button={button.Bounds}")
                        Next
                        Check(Not form.Controls.Find("LaunchVR", True).Single().Enabled, "VR button remains unavailable")
                        Check(notice.Visible AndAlso form.RectangleToScreen(form.ClientRectangle).Contains(notice.RectangleToScreen(notice.ClientRectangle)),
                              "startup failure stays visible outside scrolling tabs")
                        If style AndAlso width = 900 AndAlso (tab.Text = "Launcher" OrElse tab.Text = "Graphics") Then
                            Using bitmap As New Bitmap(form.Width, form.Height)
                                form.DrawToBitmap(bitmap, New Rectangle(Point.Empty, bitmap.Size))
                                bitmap.Save(Path.Combine(root, tab.Text.ToLowerInvariant() & "-modern.png"))
                            End Using
                        End If
                    Next
                Next
            Next
            Check(Not File.Exists(Path.Combine(root, "ui", "dirt3_game.exe")), "UI checks never create or launch the game")
            Dim experimental = DirectCast(form.Controls.Find("ExperimentalVR", True).Single(), CheckBox)
            experimental.Checked = True
            Check(form.Controls.Find("LaunchVR", True).Single().Enabled, "explicit UI opt-in and VDXR package allow experimental button")
            choice.SelectedIndex = 1
            Check(Not form.Controls.Find("LaunchVR", True).Single().Enabled AndAlso form.Controls.Find("SessionStatus", True).Single().Text.Contains("orientation"), "SteamVR selection disables interactive launch with a reason")
            experimental.Checked = False
            form.Size = New Size(1000, 740)
            Dim closingSize = form.Size
            form.Close()
            Check(WindowPreferences.Load(install).Fit(New Size(4000, 3000), form.DeviceDpi, Size.Empty) = closingSize, "actual form close saves normal window size")
            Check(Not File.Exists(install.PreferencesPath), "closing the window does not save unsaved runtime or VR render scale edits")
        End Using
        For Each closingMode In {"maximized", "minimized", "maximized-minimized"}
            Dim ctx = Context(root, "window-ui-" & closingMode)
            Dim wasMaximized = closingMode <> "minimized"
            Call (New WindowPreferences With {.Width = 800, .Height = 600, .Maximized = wasMaximized}).Save(ctx)
            Using form As New MainForm(ctx)
                form.Opacity = 0 : form.ShowInTaskbar = False
                form.StartPosition = FormStartPosition.Manual : form.Location = New Point(-30000, -30000)
                form.Show() : Application.DoEvents()
                Check((form.WindowState = FormWindowState.Maximized) = wasMaximized, "saved maximized state is restored")
                If closingMode.Contains("minimized") Then form.WindowState = FormWindowState.Minimized
                form.Close()
                Dim saved = WindowPreferences.Load(ctx)
                Check(saved.Maximized = wasMaximized AndAlso saved.Width = 800 AndAlso saved.Height = 600,
                      "closing maximized or minimized preserves the normal size and previous visible state")
            End Using
        Next
    End Sub

    Private Sub TestLaunchProgress()
        Dim started = New DateTime(2026, 10, 28, 12, 0, 0, DateTimeKind.Utc)
        Dim progress As New LaunchProgressState(41, started)
        progress.Observe(41, started, False, "", TimeSpan.FromSeconds(2))
        Check(Not progress.Finished AndAlso Not progress.WindowReady, "process start alone does not mark game ready")
        progress.Observe(41, started, False, "InitialLoading", TimeSpan.FromSeconds(3))
        Check(Not progress.Finished AndAlso progress.Message.Contains("splash"), "loading splash must not dismiss startup progress")
        progress.Observe(41, started, False, "#32770", TimeSpan.FromSeconds(4))
        Check(Not progress.Finished, "a native error dialog is not a ready game window")
        progress.Observe(41, started, False, "NeonClass_41", TimeSpan.FromSeconds(5))
        Check(progress.Finished AndAlso progress.WindowReady, "owned process window completes startup")
        Dim oldProcess As New LaunchProgressState(41, started)
        oldProcess.Observe(41, started.AddSeconds(1), False, "NeonClass_41", TimeSpan.Zero)
        Check(oldProcess.Finished AndAlso Not oldProcess.WindowReady, "reused PID with another start time cannot complete startup")
        Dim wrongProcess As New LaunchProgressState(41, started)
        wrongProcess.Observe(42, started, False, "NeonClass_41", TimeSpan.Zero)
        Check(wrongProcess.Finished AndAlso Not wrongProcess.WindowReady, "another process window cannot complete startup")
        Dim earlyExit As New LaunchProgressState(41, started)
        earlyExit.Observe(41, started, True, "", TimeSpan.FromSeconds(1), -1073741819)
        Check(earlyExit.Finished AndAlso Not earlyExit.WindowReady AndAlso earlyExit.Message.Contains("exited"), "early exit has a clear failure message")
        Check(earlyExit.Message.Contains("0xC0000005"), "failure includes the actual exit code")
        Dim timeout As New LaunchProgressState(41, started)
        timeout.Observe(41, started, False, "InitialLoading", TimeSpan.FromSeconds(90))
        Check(timeout.Finished AndAlso Not timeout.WindowReady AndAlso timeout.Message.Contains("left running"), "startup wait is bounded without killing game")
        Check(GameWindow.MayActivate(1, 1, 2) AndAlso GameWindow.MayActivate(2, 1, 2), "startup may focus game while launcher or game is foreground")
        Check(Not GameWindow.MayActivate(3, 1, 2), "startup does not steal focus from another application")
    End Sub
End Module
