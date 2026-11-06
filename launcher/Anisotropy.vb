Imports System.Globalization

' Anisotropic filtering (#15714dd2), as in DiRT2VR. DiRT 3 has no setting for it, so Wrench's renderer overrides
' the game's sampler states (src/anisotropic_filter.*). Contract: DIRT3VR_ANISOTROPY = 1 (off: trilinear) / 2 / 4 /
' 8 / 16; unset or 0 keeps the game's own filtering. The renderer needs DIRT3VR_DIAGNOSTICS=1, which VR sessions and
' the desktop sessions that install the renderer set.
Public NotInheritable Class Anisotropy
    Public Const VariableName As String = "DIRT3VR_ANISOTROPY"
    ' The choices in LauncherSettings.AnisotropyLevels order.
    Public Shared ReadOnly Names As String() = {"Use game setting", "Off", "2×", "4×", "8×", "16×"}

    Private Sub New()
    End Sub

    ' Research runs only (#24f76866, finding what makes the ground teal in VR at 16x): the renderer's raise scope and
    ' texture trace pass through from the launcher's own environment, as a test runner sets them. Session.DesktopStartInfo
    ' drops every other inherited DIRT3VR_ variable; players never set these.
    Public Shared ReadOnly ResearchVariables As String() = {"DIRT3VR_ANISOTROPY_RAISE", "DIRT3VR_ANISOTROPY_TRACE"}

    ' Passes a choice other than the game's; the game's own removes the variable.
    Public Shared Sub Configure(start As ProcessStartInfo, level As Integer)
        If Not LauncherSettings.AnisotropyLevels.Contains(level) Then Throw New IOException("Choose Use game setting, Off, 2×, 4×, 8× or 16× anisotropic filtering.")
        If level = 0 Then
            start.Environment.Remove(VariableName)
        Else
            start.Environment(VariableName) = level.ToString(CultureInfo.InvariantCulture)
        End If
        For Each research In ResearchVariables
            Dim given = Environment.GetEnvironmentVariable(research)
            If level <> 0 AndAlso Not String.IsNullOrEmpty(given) AndAlso given.Length <= 16 Then
                start.Environment(research) = given
            Else
                start.Environment.Remove(research)
            End If
        Next
    End Sub

    Public Shared Function Describe(level As Integer) As String
        Dim index = Array.IndexOf(LauncherSettings.AnisotropyLevels, level)
        Return If(index < 0, "", Names(index))
    End Function
End Class
