Imports System.Globalization
Imports System.Text.RegularExpressions

' What the last VR session actually ran with, read from its native log: the headset refresh rate (when
' the runtime offers XR_FB_display_refresh_rate) and the scene size DiRT3VR asked for against what
' DiRT 3 really rendered. Adapted from DiRT2VR's refresh-rate display and GRID2VR's measured scene.
Public NotInheritable Class LastVrSession
    Private Shared ReadOnly Requested As New Regex("XR prototype enabled headset=(\d+)x(\d+) render_scale=(\d+) scene=(\d+)x(\d+)", RegexOptions.CultureInvariant)
    Private Shared ReadOnly Mismatch As New Regex("XR scene resolution mismatch requested=(\d+)x(\d+) actual=(\d+)x(\d+)", RegexOptions.CultureInvariant)
    Private Shared ReadOnly Refresh As New Regex("XR display refresh rate=([0-9]+(?:\.[0-9]+)?) Hz", RegexOptions.CultureInvariant)
    Private Shared ReadOnly RefreshMissing As New Regex("XR display refresh rate unavailable", RegexOptions.CultureInvariant)
    Private Shared ReadOnly StereoFrame As New Regex("XR prototype main=[0-9]+ submitted=1\b", RegexOptions.CultureInvariant)
    Private Const ReadLimit As Integer = 1024 * 1024

    Public Property StartedUtc As DateTime
    Public Property RenderScale As Integer
    Public Property SceneWidth As Integer
    Public Property SceneHeight As Integer
    ' Set only when DiRT 3 rendered another size, which stops VR.
    Public Property ActualWidth As Integer
    Public Property ActualHeight As Integer
    Public Property StereoFrames As Boolean
    ' Nothing when the log doesn't say (older builds); NaN when the runtime can't report it.
    Public Property RefreshHz As Double?

    Public Shared Function Parse(log As String, startedUtc As DateTime) As LastVrSession
        Dim scene = Requested.Match(log)
        If Not scene.Success Then Return Nothing ' VR never started rendering in this session.
        Dim result As New LastVrSession With {
            .StartedUtc = startedUtc,
            .RenderScale = Number(scene.Groups(3).Value), .SceneWidth = Number(scene.Groups(4).Value), .SceneHeight = Number(scene.Groups(5).Value),
            .StereoFrames = StereoFrame.IsMatch(log)}
        Dim wrong = Mismatch.Match(log)
        If wrong.Success Then result.ActualWidth = Number(wrong.Groups(3).Value) : result.ActualHeight = Number(wrong.Groups(4).Value)
        Dim rate = Refresh.Match(log)
        If rate.Success Then
            result.RefreshHz = Double.Parse(rate.Groups(1).Value, CultureInfo.InvariantCulture)
        ElseIf RefreshMissing.IsMatch(log) Then
            result.RefreshHz = Double.NaN
        End If
        Return result
    End Function

    Private Shared Function Number(text As String) As Integer
        Dim value As Integer
        Return If(Integer.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, value), value, 0)
    End Function

    ' The newest logs\session-… folder with a native log, or Nothing.
    Public Shared Function Load(context As InstallContext) As LastVrSession
        Try
            Dim logs = Path.Combine(context.UserRoot, "logs")
            If Not Directory.Exists(logs) Then Return Nothing
            SafeFiles.NoLinks(logs)
            Dim sessions = Directory.EnumerateDirectories(logs, "session-*").Take(5000).
                OrderByDescending(Function(folder) Directory.GetLastWriteTimeUtc(folder))
            For Each folder In sessions
                Dim native = Directory.EnumerateFiles(folder, "native-*.log").OrderByDescending(Function(file) IO.File.GetLastWriteTimeUtc(file)).FirstOrDefault()
                If native Is Nothing Then Continue For
                SafeFiles.NoLinks(native)
                Dim text As String
                Using stream As New FileStream(native, FileMode.Open, FileAccess.Read, FileShare.ReadWrite Or FileShare.Delete), reader As New StreamReader(stream)
                    Dim buffer(ReadLimit - 1) As Char
                    text = New String(buffer, 0, reader.ReadBlock(buffer, 0, buffer.Length))
                End Using
                Return Parse(text, Directory.GetCreationTimeUtc(folder))
            Next
        Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException
            ' Diagnostics only: an unreadable log simply shows nothing.
        End Try
        Return Nothing
    End Function

    Public Function Describe() As String
        Dim parts As New List(Of String)
        If RefreshHz.HasValue Then
            parts.Add(If(Double.IsNaN(RefreshHz.Value), "refresh rate not reported by the runtime", "headset refresh " & RefreshHz.Value.ToString("0.#", CultureInfo.InvariantCulture) & " Hz"))
        End If
        Dim requestedSize = SceneWidth & " × " & SceneHeight & " per eye at " & RenderScale & "%"
        If ActualWidth > 0 Then
            parts.Add("DiRT 3 rendered " & ActualWidth & " × " & ActualHeight & " instead of the requested " & requestedSize & ", so VR stopped")
        ElseIf StereoFrames Then
            parts.Add("scene " & requestedSize & ", as requested")
        Else
            parts.Add("requested scene " & requestedSize & "; no stereo frame was shown, so the actual size is unknown")
        End If
        Return "Last VR session (" & StartedUtc.ToLocalTime().ToString("d MMM HH:mm", CultureInfo.CurrentCulture) & "): " & String.Join(" · ", parts) & "."
    End Function
End Class
