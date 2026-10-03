using UnityEngine;

namespace StoryCycling.WorldGen.Editor
{
    // Objekte der Welt anlegen — im Editor (Szene wird gespeichert) als Prefab-Instanz, auf dem Gerät (Weltbau zur Laufzeit) als normale Kopie.
    public static class WorldSpawn
    {
        public static GameObject Spawn(GameObject prefab, Transform parent)
        {
#if UNITY_EDITOR
            if (!Application.isPlaying) return (GameObject)UnityEditor.PrefabUtility.InstantiatePrefab(prefab, parent);
#endif
            return Object.Instantiate(prefab, parent, false);
        }

        // Geändertes Asset (z. B. Material) für das Speichern im Editor markieren; zur Laufzeit nichts zu tun
        public static void MarkDirty(Object o)
        {
#if UNITY_EDITOR
            if (!Application.isPlaying) UnityEditor.EditorUtility.SetDirty(o);
#endif
        }
    }
}
