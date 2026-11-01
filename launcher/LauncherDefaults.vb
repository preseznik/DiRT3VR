' Restore graphics defaults / Restore launcher defaults (adapted from GRID2VR). Both only produce new
' control values; nothing is saved until Save settings or Launch, as when loading a settings profile.
Public NotInheritable Class LauncherDefaults
    Private Sub New()
    End Sub

    ' Desktop display choices back to "Use game setting" and VR render scale to 100%.
    Public Shared Function Graphics(current As LauncherSettings) As LauncherSettings
        Dim result = Copy(current)
        Dim defaults As New LauncherSettings()
        result.DesktopWidth = defaults.DesktopWidth : result.DesktopHeight = defaults.DesktopHeight
        result.DesktopDisplayMode = defaults.DesktopDisplayMode : result.DesktopVSync = defaults.DesktopVSync
        result.VrRenderScale = defaults.VrRenderScale
        Return result
    End Function

    ' Every launcher choice back to its default, except where VR runtimes are installed on this PC and
    ' the experimental VR opt-in, which is a consent rather than a preference.
    Public Shared Function All(current As LauncherSettings) As LauncherSettings
        Return New LauncherSettings With {
            .SteamRuntimeManifest = current.SteamRuntimeManifest,
            .VdxrRuntimeManifest = current.VdxrRuntimeManifest,
            .ExperimentalVrEnabled = current.ExperimentalVrEnabled}
    End Function

    Private Shared Function Copy(value As LauncherSettings) As LauncherSettings
        Return System.Text.Json.JsonSerializer.Deserialize(Of LauncherSettings)(System.Text.Json.JsonSerializer.Serialize(value))
    End Function
End Class
