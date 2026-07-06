using System.Collections.Generic;

namespace MotoRouteFinder.Models;

public record DebugStemEvent
{
    public int AttemptNumber { get; set; }
    public string SegmentRole { get; set; } = "";
    public int SegmentIndex { get; set; }
    public double SegmentLengthM { get; set; }
    public double CumulativeDistanceM { get; set; }
    public double SegmentBearing { get; set; }
    public int FirstHalfPoints { get; set; }
    public int SecondHalfPoints { get; set; }
    public int CloseCount { get; set; }
    public int OpposedCount { get; set; }
    public int Examined { get; set; }
    public double CloseRatio { get; set; }
    public double OpposedRatio { get; set; }
    public double OverlapWithPriorSegments { get; set; }

    public string Resolution { get; set; } = "";
    public double[] OriginalWaypoint { get; set; } = new double[2];
    public double[]? FinalWaypoint { get; set; }
    public double FinalWaypointDistanceM { get; set; }
    public double PushOverlapBefore { get; set; }
    public double PushOverlapAfter { get; set; }

    public string? RoadType { get; set; }
    public double MidpointLat { get; set; }
    public double MidpointLon { get; set; }
    public double DistanceFromStartM { get; set; }
    public double SectorFromStart { get; set; }
    public double SegmentCurvature { get; set; }
    public int EdgeCount { get; set; }
    public string? CandidateRoadClass { get; set; }
    public long SegmentBuildMs { get; set; }

    // Near-miss tracking (stem detection sensitivity)
    public double NearestNearMissM { get; set; }

    // Return distance accuracy
    public double EstReturnHaversineKm { get; set; }
    public double EstReturnWithMultiplierKm { get; set; }
    public double ActualReturnRoutedKm { get; set; }

    // StraightLineRatio (totalDist / straightDist)
    public double StraightLineRatio { get; set; }

    // Edge identity in overlap
    public int OverlappingEdgeCount { get; set; }
    public List<uint>? OverlappingEdgeIds { get; set; }

    // Diagnostic fields for route quality improvement
    public double HaversineToRoutedRatio { get; set; }
    public bool IsReturnPathOverlap { get; set; }
    public string? RoadQualityAtMidpoint { get; set; }
    public int SegmentEdgeDensityAtMidpoint { get; set; }
    public int ConnectivityAtStart { get; set; }
    public int ConnectivityAtEnd { get; set; }
}
