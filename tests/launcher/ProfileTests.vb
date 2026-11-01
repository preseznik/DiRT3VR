Imports DiRT3VR
Imports System.Drawing
Imports System.IO
Imports System.Text
Imports System.Text.Json
Imports System.Windows.Forms

Module ProfileTests
    Private Sub Reject(action As Action, check As Action(Of Boolean, String), label As String)
        Dim refused = False
        Try
            action()
        Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is JsonException OrElse TypeOf ex Is UnauthorizedAccessException
            refused = True
        End Try
        check(refused, label)
    End Sub

    Private Function Context(root As String, name As String) As InstallContext
        Dim game = Path.Combine(root, name)
        Directory.CreateDirectory(game)
        Return New InstallContext(game, Path.Combine(root, "profile-users"), Path.Combine(game, "graphics.xml"), root)
    End Function

    Private Function Ancestors(control As Control) As IEnumerable(Of Control)
        Dim chain As New List(Of Control)
        Dim current = control
        While current IsNot Nothing
            chain.Add(current) : current = current.Parent
        End While
        Return chain
    End Function

    ' Visible and Enabled already include every parent, so the topmost hidden or
    ' disabled control in the chain is the one that blocks the click.
    Private Function Blocker(control As Control) As String
        For Each item In Ancestors(control).Reverse()
            Dim description = If(String.IsNullOrEmpty(item.Name), item.GetType().Name, item.Name) & If(TypeOf item Is TabPage, " (" & item.Text & " tab)", "")
            If Not item.Visible Then Return description & " is hidden"
            If Not item.Enabled Then Return description & " is disabled"
        Next
        Return "nothing hidden or disabled"
    End Function

    ' Button.PerformClick silently does nothing unless the button can be selected: its
    ' tab page must be showing and every parent visible and enabled. A skipped click
    ' used to surface only later, as "Sequence contains no elements" from the store.
    ' Bring the button's tab forward, give the form a bounded moment to settle, then
    ' fail with the blocking control instead of clicking into nothing.
    Private Sub ClickButton(form As Form, buttonName As String, check As Action(Of Boolean, String))
        Dim button = DirectCast(form.Controls.Find(buttonName, True).Single(), Button)
        Dim page = Ancestors(button).OfType(Of TabPage)().FirstOrDefault()
        Dim deadline = Environment.TickCount64 + 2000
        Dim firstBlocker As String = Nothing
        Do
            Dim tabs = TryCast(page?.Parent, TabControl)
            If tabs IsNot Nothing AndAlso tabs.SelectedTab IsNot page Then tabs.SelectedTab = page
            Application.DoEvents()
            If button.CanSelect OrElse Environment.TickCount64 >= deadline Then Exit Do
            If firstBlocker Is Nothing Then firstBlocker = Blocker(button)
            System.Threading.Thread.Sleep(20)
        Loop
        If firstBlocker IsNot Nothing AndAlso button.CanSelect Then Console.WriteLine($"Note: {buttonName} became clickable after a wait; at first {firstBlocker}.")
        check(button.CanSelect, $"{buttonName} can be clicked ({Blocker(button)})")
        button.PerformClick()
    End Sub

    Private Function PanelMessage(form As Form) As String
        Return DirectCast(form.Controls.Find("SettingsProfileMessage", True).Single(), Label).Text
    End Function

    Public Sub Run(root As String, check As Action(Of Boolean, String))
        Dim ctx = Context(root, "profiles"), other = Context(root, "profiles-other")
        Dim store As New SettingsProfileStore(ctx)
        check(store.List().Count = 0 AndAlso Not Directory.Exists(store.Folder), "listing absent profiles does not create files")
        File.WriteAllText(ctx.GraphicsPath, "untouched game graphics")
        Dim local As New LauncherSettings With {.SteamRuntimeManifest = "C:\local\steam.json", .VdxrRuntimeManifest = "C:\local\vdxr.json"}
        local.Save(ctx)
        Dim preferencesHash = SafeFiles.Hash(ctx.PreferencesPath), graphicsHash = SafeFiles.Hash(ctx.GraphicsPath)
        Dim values = ProfileValues.Capture(New LauncherSettings With {.ModernInterface = False, .RuntimeMode = "steamvr", .DiagnosticLogging = True, .ExperimentalVrEnabled = True,
            .VrRenderScale = 75, .DesktopWidth = 1920, .DesktopHeight = 1080, .DesktopDisplayMode = "windowed", .DesktopVSync = "on", .SteamRuntimeManifest = "C:\foreign\steam.json", .SkipStartupMovies = True})
        Dim entry = store.Create("  Rally  ", values)
        check(entry.Profile.Name = "Rally" AndAlso store.List().Count = 1, "profile creation trims name and lists it")
        Dim applied = store.Read(entry.Id).Profile.Settings.ApplyTo(local)
        check(Not applied.ModernInterface AndAlso applied.RuntimeMode = "steamvr" AndAlso applied.DiagnosticLogging AndAlso applied.ExperimentalVrEnabled AndAlso applied.VrRenderScale = 75, "all portable VR and appearance choices round trip")
        check(applied.DesktopWidth = 1920 AndAlso applied.DesktopHeight = 1080 AndAlso applied.DesktopDisplayMode = "windowed" AndAlso applied.DesktopVSync = "on", "all desktop profile choices round trip")
        check(applied.SkipStartupMovies AndAlso entry.Profile.Schema = 3, "startup choice is included in schema 3 profiles")
        check(applied.SteamRuntimeManifest = local.SteamRuntimeManifest AndAlso applied.VdxrRuntimeManifest = local.VdxrRuntimeManifest, "loading preserves local runtime paths")
        check(local.VrRenderScale = 100 AndAlso local.RuntimeMode = "vdxr", "loading creates a distinct settings snapshot")
        check(New SettingsProfileStore(other).List().Count = 0, "profiles are isolated per installation")
        For Each invalid In {Nothing, " ", New String("x"c, 65), "a" & vbLf & "b"}
            Reject(Sub() store.Create(invalid, values), check, "invalid profile name rejected")
        Next
        Reject(Sub() store.Create("rALLY", values), check, "case-insensitive duplicate name cannot overwrite a profile")
        Reject(Sub() store.Read("..\settings"), check, "profile identifiers cannot escape their store")
        Reject(Sub() store.Create("Invalid scale", New ProfileValues With {.VrRenderScale = 301}), check, "profile capture rejects out-of-range scale")
        Reject(Sub() store.Create("Invalid size", New ProfileValues With {.DesktopWidth = 1920}), check, "profile capture rejects incomplete desktop dimensions")
        Dim renamed = store.Update(entry, "Night", entry.Profile.Settings)
        check(renamed.Id = entry.Id AndAlso renamed.Profile.Name = "Night" AndAlso renamed.Profile.Settings.VrRenderScale = 75, "rename preserves identity and settings")
        Reject(Sub() store.Update(entry, "Stale update", values), check, "stale profile update preserves external changes")
        Reject(Sub() store.Delete(entry), check, "stale profile deletion is refused")
        Dim updated = store.Update(renamed, "Night", New ProfileValues With {.VrRenderScale = 125})
        check(store.Read(entry.Id).Profile.Settings.VrRenderScale = 125, "explicit update replaces selected profile settings")
        Dim exportPath = Path.Combine(root, "portable-profile.json")
        store.Export(updated, exportPath)
        Dim exported = File.ReadAllText(exportPath)
        check(Not exported.Contains("Manifest") AndAlso Not exported.Contains("GameRoot") AndAlso Not exported.Contains("foreign"), "export excludes machine paths")
        Dim imported = store.Import(exportPath), importedAgain = store.Import(exportPath)
        check(imported.Profile.Name = "Night (2)" AndAlso importedAgain.Profile.Name = "Night (3)" AndAlso imported.Id <> entry.Id, "import uses fresh identifiers and unique names without overwrite")
        check(imported.Profile.Settings.VrRenderScale = 125, "versioned export/import retains saved choices")
        Dim foreignStore As New SettingsProfileStore(other)
        check(foreignStore.Import(exportPath).Profile.Name = "Night", "export can be explicitly imported into another installation")
        Reject(Sub() store.Export(updated, Path.Combine(store.Folder, "collision.json")), check, "export cannot overwrite internal profile storage")
        Dim importPath = Path.Combine(root, "invalid-profile.json")
        For Each bad In {"", "null", "{}", "{broken", exported.Replace("DiRT3VR.SettingsProfile", "DiRT2VR.SettingsProfile"), exported.Replace("""Schema"": 3", """Schema"": 99"),
                         exported.Replace("""VrRenderScale"": 125", """VrRenderScale"": 999"), exported.Replace("""RuntimeMode"": ""vdxr""", """RuntimeMode"": null"),
                         exported.Replace("""Name"": ""Night""", """Name"": null"), exported.Replace("""Settings"": {", """Settings"": {""VdxrRuntimeManifest"":""foreign.json"","),
                         exported.Replace("""Schema"": 3", """Schema"": 3, ""schema"": 3"), exported.Replace("""VrRenderScale"": 125", """VrRenderScale"": 125, ""vrRenderScale"": 50"), New String(" "c, 65537)}
            File.WriteAllText(importPath, bad)
            Reject(Sub() store.Import(importPath), check, "malformed, foreign, unsupported or unsafe import rejected")
        Next
        check(store.List().Count = 3 AndAlso store.Read(updated.Id).Revision = updated.Revision, "failed imports leave existing profiles unchanged")
        File.WriteAllText(importPath, exported, New UTF8Encoding(True))
        check(store.Import(importPath).Profile.Name = "Night (4)", "UTF-8 BOM exported by Windows tools is supported")
        Dim brokenId = Guid.NewGuid().ToString("N")
        File.WriteAllText(Path.Combine(store.Folder, brokenId & ".json"), "{broken")
        Dim listed = store.List()
        check(listed.Count = 5 AndAlso listed.Single(Function(item) item.Id = brokenId).Problem IsNot Nothing, "one corrupt profile is visible and does not hide healthy profiles")
        store.Delete(updated)
        check(Not File.Exists(Path.Combine(store.Folder, updated.Id & ".json")) AndAlso store.Read(imported.Id).Profile IsNot Nothing, "delete removes only the selected settings profile")
        check(SafeFiles.Hash(ctx.PreferencesPath) = preferencesHash AndAlso SafeFiles.Hash(ctx.GraphicsPath) = graphicsHash, "all profile operations preserve active preferences and game graphics")
        check(Not File.Exists(Path.Combine(ctx.GameRoot, "dirt3_game.exe")), "profile tests never launch or create a game executable")
        check(Directory.GetFiles(store.Folder, "*.tmp").Length = 0, "successful profile writes leave no temporary files")
        Dim bounded As New SettingsProfileStore(Context(root, "profiles-limit"))
        Directory.CreateDirectory(bounded.Folder)
        For index = 1 To 128
            File.WriteAllText(Path.Combine(bounded.Folder, Guid.NewGuid().ToString("N") & ".json"), exported.Replace("""Name"": ""Night""", $"""Name"": ""Profile {index}"""))
            File.WriteAllText(Path.Combine(bounded.Folder, $"unrelated-{index}.json"), "unrelated file")
        Next
        check(bounded.List().Count = 128, "profile bound counts only owned filename format and ignores unrelated JSON files")
        Reject(Sub() bounded.Create("One too many", values), check, "profile count limit refuses additional files")
        check(File.ReadAllText(Path.Combine(bounded.Folder, "unrelated-1.json")) = "unrelated file", "unrelated store files are preserved")
        Dim legacyPath = Path.Combine(root, "legacy-settings-profile.json")
        File.WriteAllText(legacyPath, "{""Kind"":""DiRT3VR.SettingsProfile"",""Schema"":1,""Name"":""Legacy"",""Settings"":{""VrRenderScale"":125}}")
        Dim legacy = foreignStore.Import(legacyPath)
        check(legacy.Profile.Schema = 3 AndAlso Not legacy.Profile.Settings.SkipStartupMovies AndAlso legacy.Profile.Settings.VrRenderScale = 125, "schema 1 profiles migrate with startup movies retained")
        File.WriteAllText(legacyPath, "{""Kind"":""DiRT3VR.SettingsProfile"",""Schema"":1,""Name"":""Invalid legacy"",""Settings"":{""SkipStartupMovies"":true}}")
        Reject(Sub() foreignStore.Import(legacyPath), check, "schema 1 cannot silently opt into a newer startup behavior")
    End Sub

    Public Sub RunUi(root As String, check As Action(Of Boolean, String))
        Dim ctx = Context(root, "profiles-ui")
        Dim initial As New LauncherSettings With {.SteamRuntimeManifest = "C:\local\steam.json", .VdxrRuntimeManifest = "C:\local\vdxr.json"}
        initial.Save(ctx)
        Dim before = SafeFiles.Hash(ctx.PreferencesPath)
        Using form As New MainForm(ctx)
            form.ShowInTaskbar = False : form.StartPosition = FormStartPosition.Manual : form.Location = New Point(-30000, -30000)
            form.Show() : Application.DoEvents()
            Dim tabs = DirectCast(form.Controls.Find("LauncherTabs", True).Single(), TabControl)
            tabs.SelectedIndex = 3 : Application.DoEvents()
            Dim name = DirectCast(form.Controls.Find("SettingsProfileName", True).Single(), TextBox)
            Dim choices = DirectCast(form.Controls.Find("SettingsProfileChoice", True).Single(), ComboBox)
            Dim scale = DirectCast(form.Controls.Find("VrRenderScale", True).Single(), ValueSlider)
            Dim modern = DirectCast(form.Controls.Find("ModernInterface", True).Single(), CheckBox)
            check(Not form.HasUnsavedSettings(), "initial settings controls match the saved baseline")
            scale.Value = 150 : name.Text = "Desk and VR"
            ClickButton(form, "CreateSettingsProfile", check)
            Dim store As New SettingsProfileStore(ctx), listed = store.List()
            ' The handler reports store errors in the panel instead of throwing, so show that text.
            check(listed.Count = 1, $"Save as new profile creates exactly one profile through the actual button handler (found {listed.Count}; panel says: {PanelMessage(form)})")
            Dim entry = listed(0)
            check(entry.Profile.Settings.VrRenderScale = 150 AndAlso choices.Items.Count = 1, "Save as new captures unsaved controls through actual button handler")
            check(SafeFiles.Hash(ctx.PreferencesPath) = before AndAlso form.HasUnsavedSettings(), "saving a named profile does not silently save current preferences")
            check(PanelMessage(form).Contains("launcher's settings were not changed"), "Save as new profile says the launcher's settings were not saved")
            name.Text = "Renamed"
            ClickButton(form, "RenameSettingsProfile", check)
            check(store.Read(entry.Id).Profile.Name = "Renamed", $"rename button persists the new display name (panel says: {PanelMessage(form)})")
            Dim replacement = store.Create("Low resolution", New ProfileValues With {.ModernInterface = False, .VrRenderScale = 75, .DesktopWidth = 1280, .DesktopHeight = 720, .DesktopDisplayMode = "windowed", .DesktopVSync = "off"})
            Dim asked = 0
            check(Not form.LoadSettingsProfile(replacement.Profile, Function()
                                                                       asked += 1
                                                                       Return False
                                                                   End Function), "cancelled load refuses to discard unsaved edits")
            check(asked = 1 AndAlso scale.Value = 150 AndAlso modern.Checked AndAlso tabs.SelectedIndex = 3, "cancel preserves every edited control and selected tab")
            check(form.LoadSettingsProfile(replacement.Profile, Function() True), "explicit replacement loads profile into controls")
            Dim current = form.CurrentSettings()
            check(current.VrRenderScale = 75 AndAlso Not modern.Checked AndAlso current.DesktopWidth = 1280 AndAlso current.DesktopHeight = 720 AndAlso current.DesktopVSync = "off", "loaded profile updates desktop, VR and appearance controls")
            check(current.VdxrRuntimeManifest = initial.VdxrRuntimeManifest AndAlso tabs.SelectedIndex = 3, "profile load retains local paths and selected tab")
            check(form.Controls.Find("Notice", True).Single().Text.Contains("Use Save settings"), "loaded profile replaces stale saved-status text")
            check(SafeFiles.Hash(ctx.PreferencesPath) = before AndAlso form.HasUnsavedSettings(), "load remains unsaved until explicit Save settings")
            Dim profileFiles = Directory.GetFiles(store.Folder).Select(Function(filename) SafeFiles.Hash(filename)).OrderBy(Function(hash) hash).ToArray()
            ClickButton(form, "SaveSettings", check)
            check(Not form.HasUnsavedSettings() AndAlso LauncherSettings.Load(ctx).VrRenderScale = 75, "Save settings persists a loaded profile and resets dirty state")
            check(Directory.GetFiles(store.Folder).Select(Function(filename) SafeFiles.Hash(filename)).OrderBy(Function(hash) hash).SequenceEqual(profileFiles), "Save settings leaves every settings profile unchanged")
            check(form.Controls.Find("Notice", True).Single().Text.Contains("profiles were not changed"), "Save settings says profiles were not changed")
            Dim panel = DirectCast(form.Controls.Find("SettingsProfiles", True).Single(), SettingsProfilePanel)
            panel.Reload(entry.Id)
            ClickButton(form, "LoadSettingsProfile", check)
            check(scale.Value = 150 AndAlso LauncherSettings.Load(ctx).VrRenderScale = 75, $"Load button updates controls without saving preferences (panel says: {PanelMessage(form)})")
            form.LoadSettingsProfile(replacement.Profile, Function() True)
            For Each style In {False, True}
                modern.Checked = style
                For Each width In {560, 900, 1440}
                    form.ClientSize = New Size(Px(form, width), Px(form, 760)) : Application.DoEvents()
                    check(Not tabs.SelectedTab.HorizontalScroll.Visible, "profile panel fits narrow and wide Modern/Classic layouts")
                    check(DirectCast(choices.SelectedItem, SettingsProfileEntry).Id = entry.Id AndAlso scale.Value = 75, "theme and sizing preserve profile selection and current edits")
                    Using bitmap As New Bitmap(form.Width, form.Height)
                        form.DrawToBitmap(bitmap, New Rectangle(Point.Empty, bitmap.Size))
                        bitmap.Save(Path.Combine(root, $"profiles-{If(style, "modern", "classic")}-{width}.png"))
                    End Using
                Next
            Next
            scale.Value = 200
            panel.Reload(entry.Id)
            check(scale.Value = 200, "refreshing profile list never loads settings")
            Dim empty = Context(root, "profiles-empty-ui")
            panel.SetContext(empty)
            check(choices.Items.Count = 0 AndAlso Not form.Controls.Find("LoadSettingsProfile", True).Single().Enabled, "installation switch clears old profile selection")
            form.Close()
            check(LauncherSettings.Load(ctx).VrRenderScale = 75, "closing does not save later unsaved edits")
        End Using
    End Sub
End Module
