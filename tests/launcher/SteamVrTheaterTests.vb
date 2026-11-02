Imports DiRT3VR
Imports System.IO

' SteamVR's desktop theater off for SteamVR sessions (#24867b69, GRID2VR's tools/steamvr_theater.py): the
' journal-first change and the restore, with a fake settings interface (the OpenVR binding needs SteamVR).
Module SteamVrTheaterTests
    Private NotInheritable Class FakeSetting
        Implements ITheaterSetting
        Public Value As Boolean
        Public Overridden As Boolean
        Public Calls As New List(Of String)
        Public IgnoreSet As Boolean
        Public [Default] As Boolean = True

        Public Function GetValue() As Boolean Implements ITheaterSetting.GetValue
            Return Value
        End Function

        Public Sub SetValue(value As Boolean) Implements ITheaterSetting.SetValue
            Calls.Add("set " & value)
            If Not IgnoreSet Then Me.Value = value : Overridden = True
        End Sub

        Public Sub Remove() Implements ITheaterSetting.Remove
            Calls.Add("remove")
            Overridden = False : Value = [Default]
        End Sub
    End Class

    Private Function Refused(run As Action) As Boolean
        Try
            run()
        Catch ex As IOException
            Return True
        End Try
        Return False
    End Function

    Public Sub Run(root As String, check As Action(Of Boolean, String))
        Dim folder = Path.Combine(root, "steamvr-theater")
        Directory.CreateDirectory(folder)
        Dim defaults = Path.Combine(folder, "default.vrsettings"), user = Path.Combine(folder, "steamvr.vrsettings"), journal = Path.Combine(folder, "pending.json")

        ' What SteamVR is configured with: its default unless the player's file overrides it.
        File.WriteAllText(defaults, "{ ""dashboard"" : { ""autoShowGameTheater"" : true, ""other"" : 1 }, }")
        Dim configured = SteamVrTheater.Configured(defaults, user)
        check(configured.Before AndAlso Not configured.HadOverride AndAlso configured.Default, "no user file: SteamVR's default (on)")
        File.WriteAllText(user, "{ ""dashboard"" : { ""autoShowGameTheater"" : false } }")
        configured = SteamVrTheater.Configured(defaults, user)
        check(Not configured.Before AndAlso configured.HadOverride, "the player's own setting wins")
        File.WriteAllText(user, "{ ""steamvr"" : { ""mirrorView"" : true } }")
        check(SteamVrTheater.Configured(defaults, user).Before AndAlso Not SteamVrTheater.Configured(defaults, user).HadOverride, "a user file without the key keeps the default")
        File.WriteAllText(user, "{ ""dashboard"" : { ""autoShowGameTheater"" : ""yes"" } }")
        check(Refused(Sub() SteamVrTheater.Configured(defaults, user)), "a setting that isn't true/false is refused")
        File.Delete(user)

        ' Turned off for the session: the journal exists first; the default comes back by removing the key.
        Dim api As New FakeSetting With {.Value = True}
        check(SteamVrTheater.Begin(api, journal, SteamVrTheater.Configured(defaults, user)) = "temporarily_disabled" AndAlso Not api.Value AndAlso File.Exists(journal), "the theater is off for the session, with a journal")
        check(Refused(Sub() SteamVrTheater.Begin(api, journal, SteamVrTheater.Configured(defaults, user))), "a second change waits for the pending one")
        check(SteamVrTheater.Restore(api, journal) = "restored_default" AndAlso api.Value AndAlso Not api.Overridden AndAlso Not File.Exists(journal) AndAlso api.Calls.Last() = "remove", "after the session the default comes back, with no leftover override")
        check(SteamVrTheater.Restore(api, journal) = "no_pending_change", "nothing to restore twice")

        ' A player who had it on explicitly gets their own value back.
        File.WriteAllText(user, "{ ""dashboard"" : { ""autoShowGameTheater"" : true } }")
        api = New FakeSetting With {.Value = True, .Overridden = True}
        SteamVrTheater.Begin(api, journal, SteamVrTheater.Configured(defaults, user))
        check(SteamVrTheater.Restore(api, journal) = "restored_override" AndAlso api.Value AndAlso api.Overridden, "an explicit setting is set back, not removed")

        ' Already off: nothing changes and nothing is journaled.
        File.WriteAllText(user, "{ ""dashboard"" : { ""autoShowGameTheater"" : false } }")
        api = New FakeSetting With {.Value = False, .Overridden = True}
        check(SteamVrTheater.Begin(api, journal, SteamVrTheater.Configured(defaults, user)) = "already_disabled" AndAlso Not File.Exists(journal) AndAlso api.Calls.Count = 0, "a theater already off is left alone")

        ' The live value must match the saved one; a failed change keeps the journal.
        File.Delete(user)
        api = New FakeSetting With {.Value = False}
        check(Refused(Sub() SteamVrTheater.Begin(api, journal, SteamVrTheater.Configured(defaults, user))) AndAlso Not File.Exists(journal) AndAlso api.Calls.Count = 0, "a live value that differs from the files changes nothing")
        api = New FakeSetting With {.Value = True, .IgnoreSet = True}
        check(Refused(Sub() SteamVrTheater.Begin(api, journal, SteamVrTheater.Configured(defaults, user))) AndAlso File.Exists(journal), "an unverified change keeps the journal")
        api.IgnoreSet = False
        check(SteamVrTheater.Restore(api, journal) = "original_value_already_present" AndAlso Not File.Exists(journal), "a value already back is kept as it is")

        ' A player who turned it back on meanwhile keeps that; a damaged journal is refused and kept.
        api = New FakeSetting With {.Value = True}
        SteamVrTheater.Begin(api, journal, SteamVrTheater.Configured(defaults, user))
        api.Value = True : api.Overridden = True
        check(SteamVrTheater.Restore(api, journal) = "original_value_already_present" AndAlso api.Overridden, "the player's own change during the session is kept")
        File.WriteAllText(journal, "{""Schema"":1,""Section"":""dashboard"",""Key"":""other"",""Before"":true,""HadOverride"":true,""Default"":true}")
        check(Refused(Sub() SteamVrTheater.Restore(New FakeSetting, journal)) AndAlso File.Exists(journal), "a journal for another setting is refused and kept")
        File.Delete(journal)

        ' Not a SteamVR session, or no SteamVR here: nothing happens, the launch goes on.
        Dim ctx As New InstallContext(folder, Path.Combine(root, "users"), Path.Combine(folder, "graphics.xml"), folder)
        check(SteamVrTheater.Suppress(ctx, New RuntimeInfo With {.Mode = "vdxr", .ManifestPath = "C:\VDXR\virtualdesktop-openxr-32.json"}) = "" AndAlso Not SteamVrTheater.Pending(ctx), "VDXR sessions leave SteamVR alone")
        Dim note = SteamVrTheater.Suppress(ctx, New RuntimeInfo With {.Mode = "steamvr", .ManifestPath = Path.Combine(folder, "no-steamvr", "steamxr_win32.json")})
        check(note.StartsWith("SteamVR's desktop theater was left as it is", StringComparison.Ordinal) AndAlso Not SteamVrTheater.Pending(ctx), "without SteamVR the theater is left as it is, with a note (" & note & ")")
        check(SteamVrTheater.RestorePending(ctx, folder) = "", "nothing pending, nothing to restore")
    End Sub
End Module
