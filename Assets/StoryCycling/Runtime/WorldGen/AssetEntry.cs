using UnityEngine;

namespace StoryCycling.WorldGen
{
    public enum AssetCategory
    {
        Landmark,
        Vegetation,
        Building,
        Prop,
        RoadFurniture,
        GroundCover
    }

    // One entry per prefab the world generator is allowed to instantiate. The
    // generator is a *placer*, not an *inventor*: it may only use these assets.
    [CreateAssetMenu(fileName = "AssetEntry", menuName = "Story Cycling/WorldGen/Asset Entry")]
    public class AssetEntry : ScriptableObject
    {
        [Header("Source")]
        public GameObject prefab;

        [Header("Placement metadata")]
        public AssetCategory category;
        public string[] biomeTags;              // z.B. "forest", "coast", "urban", "field"
        public Vector2 scaleRange = new Vector2(0.9f, 1.2f);
        public bool randomYRotation = true;
        public float minSpacing = 3f;           // Mindestabstand zu gleicher Kategorie (m)
        public Vector2 offsetFromRoad = new Vector2(3f, 25f); // seitlicher Abstand zur Straße (m)
        public Vector2 slopeRangeDeg = new Vector2(0f, 30f);  // erlaubte Hangneigung am Boden
        public float weight = 1f;               // relative Auswahlwahrscheinlichkeit
    }
}
