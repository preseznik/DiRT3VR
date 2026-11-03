Imports System.Globalization

' "Recommend for this PC" (#68e16dc9): a VR render scale, or a quick VR preset, from the last VR session's
' measured FPS (MeasuredPerformance, #4896ffd3). Ported from the measured part of GRID2VR's
' GraphicsRecommendation. It is used only when that session ran the VR quality the launcher holds now and on
' the same headset. There is no estimate without a measurement: GRID2VR's per-GPU budget was calibrated for
' GRID 2, and DiRT 3 has none yet. A starting point, never a silent change: the launcher applies it to the
' controls with Undo, and the player saves or launches to keep it.
Public NotInheritable Class GraphicsRecommendation
    Public Const Keep As String = "keep", Balanced As String = "vrbalanced", Potato As String = "potato", Unchanged As String = "none"
    Public Const PotatoScale As Integer = 70

    Public NotInheritable Class Result
        ' Keep (the current VR quality, a new render scale), vrbalanced or potato (a quick preset), or none.
        Public Property Preset As String = Unchanged
        Public Property Scale As Integer
        Public Property Reason As String = ""
    End Class

    Private Sub New()
    End Sub

    ' The VR quality a session runs with: each VR choice, else the desktop choice; options on the game's
    ' own setting are left out.
    Public Shared Function VrQuality(settings As LauncherSettings) As SortedDictionary(Of String, String)
        Dim catalog = GraphicsCatalog.Current
        Dim result As New SortedDictionary(Of String, String)(catalog.Normalize(settings.GraphicsQuality), StringComparer.Ordinal)
        For Each pair In catalog.Normalize(settings.VrGraphicsQuality)
            result(pair.Key) = pair.Value
        Next
        Return result
    End Function

    Private Shared Function RoundFive(scale As Double) As Integer
        Return CInt(Math.Round(scale / 5, MidpointRounding.AwayFromZero)) * 5
    End Function

    ' headsetWidth/Height: the connected headset's per-eye size, 0 when unknown.
    Public Shared Function Recommend(last As LastVrSession, current As LauncherSettings, headsetWidth As Integer, headsetHeight As Integer) As Result
        Const AfterSession As String = "Launch VR and drive for at least 10 seconds, then press Recommend again: it tunes from that session's measured frame rate."
        Dim measured = last?.Performance
        If measured Is Nothing Then Return New Result With {.Reason = "No measured VR session yet. " & AfterSession}
        If Not measured.Usable OrElse measured.Scale < 50 OrElse measured.Scale > 300 Then Return New Result With {.Reason = "The last VR session was too short to measure. " & AfterSession}
        If headsetWidth > 0 AndAlso headsetHeight > 0 AndAlso (measured.HeadsetWidth <> headsetWidth OrElse measured.HeadsetHeight <> headsetHeight) Then
            Return New Result With {.Reason = "The last VR session used another headset resolution. " & AfterSession}
        End If
        Dim quality = VrQuality(current)
        If last.VrQuality Is Nothing OrElse Not last.VrQuality.OrderBy(Function(pair) pair.Key, StringComparer.Ordinal).SequenceEqual(quality) OrElse last.VrAntiAliasing <> current.VrAntiAliasing Then
            Return New Result With {.Reason = "The last VR session used other VR quality settings, so its frame rate doesn't tell how these would run. " & AfterSession}
        End If

        Dim c = CultureInfo.CurrentCulture
        Dim maximum = Math.Max(100, measured.Scale)
        Dim clamp = Function(scale As Integer) Math.Clamp(scale, 50, maximum)
        Dim target = measured.TargetFps.ToString("0", c)
        Dim rate = measured.FpsMedian.ToString("0", c) & " of " & target & " FPS"
        Dim gpuText = If(measured.GpuPercent.HasValue, measured.GpuPercent.Value.ToString("0", c) & "%", "an unknown load")
        Dim scaleText = measured.Scale & "%"
        Const CpuAdvice As String = "Fewer opponents and lower Crowd, Objects or Car reflections reduce the CPU's work."
        Select Case measured.Limit
            Case "at_target"
                If measured.Dips Then
                    ' A held median can hide slow seconds; tune for those instead.
                    Dim dips = "Your last VR session mostly held " & target & " FPS, but " &
                               If(measured.DipShare.HasValue, (measured.DipShare.Value * 100).ToString("0", c) & "% of seconds", "some seconds") & " dropped (low " & measured.FpsLow.ToString("0", c) & " FPS)"
                    Select Case measured.DipLimit
                        Case "gpu"
                            Return New Result With {.Preset = Keep, .Scale = clamp(Math.Min(RoundFive(measured.Scale * Math.Sqrt(measured.FpsLow / measured.TargetFps)), measured.Scale - 5)),
                                .Reason = dips & " because the GPU was the limit, so render scale goes down from " & scaleText & "."}
                        Case "cpu"
                            Return New Result With {.Preset = Keep, .Scale = clamp(measured.Scale), .Reason = dips & " because the CPU was the limit, so render scale stays at " & scaleText & ": lowering it won't help. " & CpuAdvice}
                        Case Else
                            Return New Result With {.Preset = Keep, .Scale = clamp(measured.Scale), .Reason = dips & " without one clear limit, so render scale stays at " & scaleText & ". Fewer opponents or a lower render scale can smooth them."}
                    End Select
                End If
                If measured.GpuPercent.HasValue AndAlso measured.GpuPercent.Value < 70 AndAlso measured.Scale < 100 Then
                    Return New Result With {.Preset = Keep, .Scale = clamp(measured.Scale + 10),
                        .Reason = "Your last VR session held " & target & " FPS with these settings and the GPU at " & gpuText & ", so render scale goes up 10%. Press Recommend again after the next session."}
                End If
                Return New Result With {.Preset = Keep, .Scale = clamp(measured.Scale),
                    .Reason = "Your last VR session held " & target & " FPS at " & scaleText & " with these settings, with the GPU at " & gpuText & ". Keep them." &
                              If(measured.GpuPercent.HasValue AndAlso measured.GpuPercent.Value < 70 AndAlso measured.BlockedMedian.GetValueOrDefault(-1) >= 1, " The spare GPU and CPU time leaves room for more opponents or heavier tracks.", "")}
            Case "gpu"
                If GraphicsCatalog.Current.MatchingPreset(quality)?.Id = "ultra" Then
                    Return New Result With {.Preset = Balanced, .Scale = clamp(measured.Scale),
                        .Reason = "Ultra was the GPU limit at " & rate & ". VR Balanced keeps High's look with a lighter crowd, so this switches to it and keeps " & clamp(measured.Scale) & "% render scale."}
                End If
                Dim lower = RoundFive(measured.Scale * Math.Sqrt(measured.FpsMedian / measured.TargetFps) * 0.95)
                If lower < 60 Then Return New Result With {.Preset = Potato, .Scale = PotatoScale, .Reason = "The GPU was the limit at " & rate & ", even at " & scaleText & ". Potato / Low-End is the lightest preset."}
                Return New Result With {.Preset = Keep, .Scale = clamp(Math.Min(lower, measured.Scale - 5)), .Reason = "The GPU was the limit at " & rate & " with these settings, so render scale goes down from " & scaleText & "."}
            Case "cpu"
                Return New Result With {.Preset = Keep, .Scale = clamp(measured.Scale),
                    .Reason = "The CPU was the limit at " & rate & " with the GPU at " & gpuText & ", so render scale stays at " & scaleText & ": lowering it won't help. " & CpuAdvice}
            Case Else
                Return New Result With {.Preset = Keep, .Scale = clamp(RoundFive(measured.Scale * 0.9)), .Reason = "Below the headset rate at " & rate & " without one clear limit, so render scale goes down 10%."}
        End Select
    End Function
End Class
