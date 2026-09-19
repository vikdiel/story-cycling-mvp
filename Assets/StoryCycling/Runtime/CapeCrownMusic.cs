using UnityEngine;
using System.Collections.Generic;

namespace StoryCycling
{
    // Ambient loop for the menus, plus a rotating set of ride tracks for the ride.
    // Ride tracks: Kevin MacLeod (incompetech.com), CC BY 4.0 — see CREDITS.md.
    public sealed class CapeCrownMusic : MonoBehaviour
    {
        private static readonly string[] RideTrackSlugs =
        {
            "raving_energy", "neon_laser", "cipher", "laser_groove", "show_your_moves",
            "getting_it_done", "bit_shift", "deuces", "outfoxing", "mining_moonlight"
        };

        private AudioSource ambient, ride;
        private AudioClip[] rideClips;
        private int rideIndex = -1;
        private bool focused = true, riding;
        public float Volume { get; private set; }
        public bool Muted { get; private set; }

        private void Awake()
        {
            Volume = PlayerPrefs.GetFloat("cape.music.volume", .32f);
            Muted = PlayerPrefs.GetInt("cape.music.muted", 0) == 1;
            ambient = AddSource("CampsBayEvening", true);
            ride = AddSource(null, false);
            var clips = new List<AudioClip>();
            foreach (string slug in RideTrackSlugs)
            {
                AudioClip clip = Resources.Load<AudioClip>("RideTracks/" + slug);
                if (clip != null) clips.Add(clip);
            }
            if (clips.Count == 0) clips.Add(Resources.Load<AudioClip>("CampsBayRide")); // fallback
            rideClips = clips.ToArray();
            // Shuffle so every session starts with a different track.
            for (int i = rideClips.Length - 1; i > 0; i--)
            {
                int j = Random.Range(0, i + 1);
                AudioClip tmp = rideClips[i]; rideClips[i] = rideClips[j]; rideClips[j] = tmp;
            }
        }

        private AudioSource AddSource(string clipName, bool loop)
        {
            var source = gameObject.AddComponent<AudioSource>();
            if (!string.IsNullOrEmpty(clipName)) source.clip = Resources.Load<AudioClip>(clipName);
            source.loop = loop; source.spatialBlend = 0; source.volume = 0; source.playOnAwake = false;
            if (source.clip != null) source.Play();
            return source;
        }

        public void SetVolume(float value) { Volume = Mathf.Clamp01(value); PlayerPrefs.SetFloat("cape.music.volume", Volume); }
        public void ToggleMute() { Muted = !Muted; PlayerPrefs.SetInt("cape.music.muted", Muted ? 1 : 0); PlayerPrefs.Save(); }
        public void SetRiding(bool value) { riding = value; }

        private void Update()
        {
            float target = focused && !Muted ? Volume : 0;
            ambient.volume = Mathf.MoveTowards(ambient.volume, riding ? target * .22f : target, Time.unscaledDeltaTime * .3f);
            if (riding)
            {
                if (rideClips.Length > 0 && !ride.isPlaying)
                {
                    rideIndex = (rideIndex + 1) % rideClips.Length;
                    ride.clip = rideClips[rideIndex];
                    ride.Play();
                }
                ride.volume = Mathf.MoveTowards(ride.volume, target, Time.unscaledDeltaTime * .7f);
            }
            else
            {
                ride.volume = Mathf.MoveTowards(ride.volume, 0, Time.unscaledDeltaTime * .7f);
                if (ride.volume <= .001f && ride.isPlaying) ride.Stop();
            }
        }

        private void OnApplicationFocus(bool focus) { focused = focus; SetPaused(!focus); }
        private void OnApplicationPause(bool pause) { focused = !pause; SetPaused(pause); PlayerPrefs.Save(); }
        private void SetPaused(bool pause) { if (pause) { ambient.Pause(); ride.Pause(); } else { ambient.UnPause(); ride.UnPause(); } }
    }
}
