Imports DiRT3VR
Imports System.Drawing
Imports System.IO
Imports System.Windows.Forms

Module RecenterTests
    Private Function Context(root As String, name As String) As InstallContext
        Dim game = Path.Combine(root, name)
        Directory.CreateDirectory(game)
        Return New InstallContext(game, Path.Combine(root, "recenter-users"), Path.Combine(game, "graphics.xml"), root)
    End Function

    Private Function Refused(action As Action) As Boolean
        Try
            action()
        Catch ex As IOException
            Return True
        End Try
        Return False
    End Function

    Public Sub Run(root As String, check As Action(Of Boolean, String))
        check(RecenterShortcut.Valid(121, 0) AndAlso RecenterShortcut.Describe(121, 0) = "F10", "F10 is the valid default recenter key")
        check(RecenterShortcut.Describe(82, 5) = "Ctrl+Shift+R" AndAlso RecenterShortcut.Describe(101, 2) = "Alt+Num 5" AndAlso RecenterShortcut.Describe(34, 0) = "Page Down", "recenter keys have readable names")
        For Each pair In {(9, 2), (115, 2), (46, 3), (46, 7), (16, 0), (17, 0), (18, 0), (27, 0), (91, 0), (60, 0), (121, 8), (121, -1), (136, 0)}
            check(Not RecenterShortcut.Valid(pair.Item1, pair.Item2), "reserved or unsupported recenter key refused: " & pair.Item1 & "/" & pair.Item2)
        Next
        check(RecenterShortcut.Valid(9, 1) AndAlso RecenterShortcut.Valid(115, 1) AndAlso RecenterShortcut.Valid(46, 1) AndAlso RecenterShortcut.Valid(135, 7), "Ctrl combinations with Tab, F4 and Delete stay allowed")
        check(RecenterShortcut.Modifiers(Keys.R Or Keys.Control Or Keys.Shift) = 5 AndAlso RecenterShortcut.Modifiers(Keys.F10 Or Keys.Alt) = 2, "WinForms modifiers map to Ctrl=1, Alt=2, Shift=4")
        check(RecenterShortcut.IsModifierOnly(Keys.ControlKey Or Keys.Control) AndAlso RecenterShortcut.IsModifierOnly(Keys.Menu Or Keys.Alt) AndAlso Not RecenterShortcut.IsModifierOnly(Keys.R Or Keys.Control), "modifier-only presses wait for the main key")

        Dim ctx = Context(root, "recenter-settings")
        Dim settings As New LauncherSettings With {.SteamRuntimeManifest = "", .VdxrRuntimeManifest = "", .RecenterKey = 82, .RecenterModifiers = 1}
        settings.Save(ctx)
        Dim loaded = LauncherSettings.Load(ctx)
        check(loaded.RecenterKey = 82 AndAlso loaded.RecenterModifiers = 1, "recenter key round trips through launcher settings")
        SafeFiles.SaveJson(ctx.PreferencesPath, New With {.Version = 1, .VrRenderScale = 150})
        loaded = LauncherSettings.Load(ctx)
        check(loaded.RecenterKey = 121 AndAlso loaded.RecenterModifiers = 0 AndAlso loaded.VrRenderScale = 150, "older settings files default to F10")
        SafeFiles.SaveJson(ctx.PreferencesPath, New With {.Version = 1, .RecenterKey = 9, .RecenterModifiers = 2})
        check(Refused(Sub() LauncherSettings.Load(ctx)), "edited settings cannot store Alt+Tab as the recenter key")

        Dim store As New SettingsProfileStore(ctx)
        Dim saved = store.Create("Wheel", ProfileValues.Capture(New LauncherSettings With {.SteamRuntimeManifest = "", .VdxrRuntimeManifest = "", .RecenterKey = 36, .RecenterModifiers = 4}))
        Dim reread = store.Read(saved.Id).Profile
        check(reread.Schema = 3 AndAlso reread.Settings.RecenterKey = 36 AndAlso reread.Settings.RecenterModifiers = 4, "settings profiles keep the recenter key as schema 3")
        check(reread.Settings.ApplyTo(New LauncherSettings With {.SteamRuntimeManifest = "", .VdxrRuntimeManifest = ""}).RecenterKey = 36, "loading a profile applies its recenter key")
        Dim importPath = Path.Combine(root, "recenter-profile.json")
        File.WriteAllText(importPath, "{""Kind"":""DiRT3VR.SettingsProfile"",""Schema"":2,""Name"":""Before recenter"",""Settings"":{""VrRenderScale"":125}}")
        Dim migrated = store.Import(importPath).Profile
        check(migrated.Schema = 3 AndAlso migrated.Settings.RecenterKey = 121 AndAlso migrated.Settings.RecenterModifiers = 0, "schema 2 profiles import with F10")
        File.WriteAllText(importPath, "{""Kind"":""DiRT3VR.SettingsProfile"",""Schema"":2,""Name"":""Too new"",""Settings"":{""RecenterKey"":82}}")
        check(Refused(Sub() store.Import(importPath)), "a schema 2 profile cannot carry a recenter key")
        File.WriteAllText(importPath, "{""Kind"":""DiRT3VR.SettingsProfile"",""Schema"":3,""Name"":""Reserved"",""Settings"":{""RecenterKey"":115,""RecenterModifiers"":2}}")
        check(Refused(Sub() store.Import(importPath)), "a profile cannot import Alt+F4 as the recenter key")
    End Sub

    Public Sub RunUi(root As String, check As Action(Of Boolean, String))
        Dim ctx = Context(root, "recenter-ui")
        Dim initial As New LauncherSettings With {.SteamRuntimeManifest = "C:\local\steam.json", .VdxrRuntimeManifest = "C:\local\vdxr.json"}
        initial.Save(ctx)
        Using form As New MainForm(ctx)
            form.ShowInTaskbar = False : form.StartPosition = FormStartPosition.Manual : form.Location = New Point(-30000, -30000)
            form.Show() : Application.DoEvents()
            Dim tabs = DirectCast(form.Controls.Find("LauncherTabs", True).Single(), TabControl)
            Dim button = DirectCast(form.Controls.Find("RecenterKey", True).Single(), Button)
            Dim hint = DirectCast(form.Controls.Find("RecenterHint", True).Single(), Label)
            Dim notice = form.Controls.Find("Notice", True).Single()
            tabs.SelectedTab = tabs.TabPages.Cast(Of TabPage)().Single(Function(page) page.Contains(button)) : Application.DoEvents()
            check(button.Text = "F10" AndAlso hint.Text.Contains("Press F10") AndAlso Not form.HasUnsavedSettings(), "Controls shows the F10 default without unsaved changes")
            check(Not form.CaptureRecenterKey(Keys.R Or Keys.Control), "keys pass through until the recenter button is clicked")
            check(button.CanSelect, "recenter button can be clicked (" & tabs.SelectedTab.Text & ")")
            button.PerformClick()
            check(button.Text = "Press a key…", "clicking the recenter button waits for a key")
            check(form.CaptureRecenterKey(Keys.ControlKey Or Keys.Control) AndAlso button.Text = "Press a key…", "holding Ctrl alone keeps waiting")
            check(form.CaptureRecenterKey(Keys.Tab Or Keys.Alt) AndAlso button.Text = "Press a key…" AndAlso notice.Text.Contains("Alt+Tab can't be used"), "Alt+Tab is refused and capture continues")
            check(form.CaptureRecenterKey(Keys.R Or Keys.Control Or Keys.Shift), "a valid key combination is captured")
            check(button.Text = "Ctrl+Shift+R" AndAlso hint.Text.Contains("Press Ctrl+Shift+R") AndAlso form.CurrentSettings().RecenterKey = 82 AndAlso form.CurrentSettings().RecenterModifiers = 5, "captured key shows and reaches the settings")
            check(form.HasUnsavedSettings() AndAlso LauncherSettings.Load(ctx).RecenterKey = 121, "a new recenter key stays unsaved until Save settings")
            check(Not form.CaptureRecenterKey(Keys.F10), "capture ends after one key")
            button.PerformClick()
            check(form.CaptureRecenterKey(Keys.Escape) AndAlso button.Text = "Ctrl+Shift+R" AndAlso notice.Text.Contains("unchanged"), "Esc cancels and keeps the previous key")
            DirectCast(form.Controls.Find("SaveSettings", True).Single(), Button).PerformClick()
            Dim saved = LauncherSettings.Load(ctx)
            check(saved.RecenterKey = 82 AndAlso saved.RecenterModifiers = 5 AndAlso Not form.HasUnsavedSettings(), "Save settings keeps the recenter key")
            Dim profile As New SettingsProfile With {.Kind = "DiRT3VR.SettingsProfile", .Schema = 3, .Name = "Home key",
                .Settings = ProfileValues.Capture(New LauncherSettings With {.SteamRuntimeManifest = "", .VdxrRuntimeManifest = "", .RecenterKey = 36, .RecenterModifiers = 0})}
            check(form.LoadSettingsProfile(profile, Function() True) AndAlso button.Text = "Home" AndAlso form.CurrentSettings().RecenterKey = 36, "loading a profile updates the recenter key")
            Using bitmap As New Bitmap(form.Width, form.Height)
                form.DrawToBitmap(bitmap, New Rectangle(Point.Empty, bitmap.Size))
                bitmap.Save(Path.Combine(root, "controls-recenter.png"))
            End Using
            form.Close()
        End Using
    End Sub
End Module
