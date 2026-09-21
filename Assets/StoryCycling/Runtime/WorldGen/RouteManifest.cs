using System;
using UnityEngine;

namespace StoryCycling.WorldGen
{
    public enum SegmentType { Gpx, Synthetic }

    // One route segment in a manifest: either a real GPX track or a synthetic stretch.
    [Serializable]
    public class RouteSegment
    {
        public SegmentType type;
        public string source;              // GPX filename (type=Gpx)
        public float lengthM;              // target length (type=Synthetic)
        public string biome = "generic";   // biome tag for the whole segment
        public string elevationProfile = "rolling"; // rolling | hilly | flat
    }

    // A landmark placed before the filler, with an exclusion zone around it.
    [Serializable]
    public class KeyElement
    {
        public string assetName;
        public float atDistanceM;
        public string side = "right";      // left | right | center
        public float offsetM = 12f;
    }

    // Describes the ordered route segments + key elements for one ride.
    [CreateAssetMenu(fileName = "RouteManifest", menuName = "Story Cycling/WorldGen/Route Manifest")]
    public class RouteManifest : ScriptableObject
    {
        public int seed = 12345;
        public RouteSegment[] segments;
        public KeyElement[] keyElements;
    }
}
