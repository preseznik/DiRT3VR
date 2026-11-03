Imports System.Globalization
Imports System.Text.Json

' The renderer's performance-summary.json from a VR session (#4896ffd3; GRID2VR's schema 1, written by Lens's
' port of its frame timing): FPS while the headset showed stereo, GPU usage and an estimate of what limited it.
' Ported from GRID2VR's MeasuredPerformance (launcher/GraphicsRecommendation.cs), the summary part only.
Public NotInheritable Class MeasuredPerformance
    Public Const FileName As String = "performance-summary.json"
    Public Const MinimumWindows As Integer = 10
    Private Const ReadLimit As Integer = 1024 * 1024

    Public Property TargetFps As Double
    Public Property FpsMedian As Double
    Public Property FpsLow As Double
    Public Property Windows As Integer
    Public Property GpuPercent As Double?
    Public Property Limit As String = "insufficient"
    Public Property HeadsetWidth As Integer
    Public Property HeadsetHeight As Integer
    Public Property Scale As Integer
    Public Property Adapter As String = ""
    ' Median render-thread time blocked on the headset or Present; under 1 ms it had no idle time.
    Public Property BlockedMedian As Double?
    ' Share of seconds below 95% of the headset rate, and what limited them.
    Public Property DipShare As Double?
    Public Property DipLimit As String = "unknown"
    ' DiRT 3's 32-bit process at its peak (#2667690f, Wrench's "memory" object), in MB: committed memory, address
    ' space in use and the address space limit (4 GB with Large Address Aware, else 2 GB). Nothing before the first
    ' sample and in older summaries.
    Public Property PeakCommittedMb As Integer?
    Public Property PeakAddressMb As Integer?
    Public Property AddressLimitMb As Integer?

    ' At least ten seconds of stereo driving, with a known limit, rate and FPS.
    Public ReadOnly Property Usable As Boolean
        Get
            Return Windows >= MinimumWindows AndAlso {"at_target", "gpu", "cpu", "mixed"}.Contains(Limit) AndAlso TargetFps > 0 AndAlso FpsMedian > 0
        End Get
    End Property

    Public ReadOnly Property Dips As Boolean
        Get
            Return If(DipShare.HasValue, DipShare.Value >= 0.05, FpsLow < TargetFps * 0.95)
        End Get
    End Property

    ' Nothing for anything but a schema 1 summary object.
    Public Shared Function Parse(summary As JsonElement) As MeasuredPerformance
        If summary.ValueKind <> JsonValueKind.Object OrElse Number(summary, "schema") <> 1 Then Return Nothing
        Dim fps = Child(summary, "fps"), headset = Child(summary, "headset"), dips = Child(summary, "dips")
        Dim adapterName = Text(Child(summary, "adapter"), "name", "")
        Dim memory = Child(summary, "memory")
        Dim megabytes = Function(key As String) As Integer?
                            Dim value = Number(memory, key)
                            Return If(value.HasValue AndAlso value.Value >= 0 AndAlso value.Value <= 1048576, CInt(value.Value), CType(Nothing, Integer?))
                        End Function
        Return New MeasuredPerformance With {.PeakCommittedMb = megabytes("peakCommittedMb"), .PeakAddressMb = megabytes("peakAddressMb"), .AddressLimitMb = megabytes("addressLimitMb"),
            .TargetFps = If(Number(summary, "targetFps"), 0), .FpsMedian = If(Number(fps, "p50"), 0), .FpsLow = If(Number(fps, "p5"), 0),
            .Windows = CInt(Math.Clamp(If(Number(fps, "windows"), 0), 0, Integer.MaxValue)), .GpuPercent = Number(Child(summary, "gpuPercent"), "p50"),
            .Limit = Text(summary, "limit", "insufficient"),
            .HeadsetWidth = CInt(Math.Clamp(If(Number(headset, "width"), 0), 0, 100000)), .HeadsetHeight = CInt(Math.Clamp(If(Number(headset, "height"), 0), 0, 100000)),
            .Scale = CInt(Math.Clamp(If(Number(headset, "scale"), 0), 0, 1000)), .Adapter = If(adapterName.Length <= 256, adapterName, ""),
            .BlockedMedian = Number(Child(summary, "blockedMs"), "p50"), .DipShare = Number(dips, "share"), .DipLimit = Text(dips, "limit", "unknown")}
    End Function

    ' The summary in a session folder, or Nothing (none yet, unreadable, or another format).
    Public Shared Function Load(folder As String) As MeasuredPerformance
        Dim file = Path.Combine(folder, FileName)
        Try
            If Not IO.File.Exists(file) Then Return Nothing
            SafeFiles.NoLinks(file)
            If New FileInfo(file).Length > ReadLimit Then Return Nothing
            Using document = JsonDocument.Parse(IO.File.ReadAllBytes(file))
                Return Parse(document.RootElement)
            End Using
        Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException OrElse TypeOf ex Is JsonException
            Return Nothing
        End Try
    End Function

    Private Shared Function Child(value As JsonElement, key As String) As JsonElement
        Dim item As JsonElement
        Return If(value.ValueKind = JsonValueKind.Object AndAlso value.TryGetProperty(key, item), item, Nothing)
    End Function

    Private Shared Function Number(value As JsonElement, key As String) As Double?
        Dim item As JsonElement, result As Double
        If value.ValueKind = JsonValueKind.Object AndAlso value.TryGetProperty(key, item) AndAlso item.ValueKind = JsonValueKind.Number AndAlso item.TryGetDouble(result) AndAlso Double.IsFinite(result) Then Return result
        Return Nothing
    End Function

    Private Shared Function Text(value As JsonElement, key As String, fallback As String) As String
        Dim item As JsonElement
        Return If(value.ValueKind = JsonValueKind.Object AndAlso value.TryGetProperty(key, item) AndAlso item.ValueKind = JsonValueKind.String, item.GetString(), fallback)
    End Function

    ' " · memory 3.1 of 4.0 GB" from the peak address space, or "" without it; near the limit DiRT 3 can run out.
    Public Function DescribeMemory() As String
        If Not PeakAddressMb.HasValue OrElse Not AddressLimitMb.HasValue OrElse AddressLimitMb.Value <= 0 Then Return ""
        Dim c = CultureInfo.CurrentCulture
        Return " · memory " & (PeakAddressMb.Value / 1024.0).ToString("0.0", c) & " of " & (AddressLimitMb.Value / 1024.0).ToString("0.0", c) & " GB" &
            If(PeakAddressMb.Value >= AddressLimitMb.Value * 0.9, " (close to the limit)", "")
    End Function

    ' "72 FPS median, 61 low (headset 90) · GPU 97% · limited by the GPU · memory 3.1 of 4.0 GB".
    Public Function Describe() As String
        If Not Usable Then Return "too short to measure FPS (at least 10 seconds of driving in VR are needed)" & DescribeMemory()
        Dim c = CultureInfo.CurrentCulture
        Dim gpu = If(GpuPercent.HasValue, "GPU " & GpuPercent.Value.ToString("0", c) & "%", "GPU usage unavailable")
        Dim limited As String
        Select Case Limit
            Case "at_target"
                If Dips Then
                    limited = "mostly holding the headset rate" & If(DipLimit = "gpu", ", GPU-limited dips", If(DipLimit = "cpu", ", CPU-limited dips", ", with dips")) &
                              If(DipShare.HasValue, " in " & (DipShare.Value * 100).ToString("0", c) & "% of seconds", "")
                Else
                    limited = "holding the headset rate"
                End If
            Case "gpu" : limited = "limited by the GPU"
            Case "cpu" : limited = "limited by the CPU"
            Case Else : limited = "below the headset rate"
        End Select
        Return FpsMedian.ToString("0", c) & " FPS median, " & FpsLow.ToString("0", c) & " low (headset " & TargetFps.ToString("0", c) & ") · " & gpu & " · " & limited & DescribeMemory()
    End Function
End Class
