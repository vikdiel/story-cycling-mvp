using UnityEngine;

namespace StoryCycling
{
    public sealed class CapeCrownMusic : MonoBehaviour
    {
        private AudioSource ambient, ride;
        private bool focused=true, riding;
        public float Volume { get; private set; }
        public bool Muted { get; private set; }
        private void Awake()
        {
            Volume=PlayerPrefs.GetFloat("cape.music.volume",.32f);
            Muted=PlayerPrefs.GetInt("cape.music.muted",0)==1;
            ambient=AddSource("CampsBayEvening");
            ride=AddSource("CampsBayRide");
        }
        private AudioSource AddSource(string clipName)
        {
            var source=gameObject.AddComponent<AudioSource>();
            source.clip=Resources.Load<AudioClip>(clipName);
            source.loop=true;source.spatialBlend=0;source.volume=0;source.playOnAwake=false;
            if(source.clip!=null)source.Play();
            return source;
        }
        public void SetVolume(float value) { Volume=Mathf.Clamp01(value);PlayerPrefs.SetFloat("cape.music.volume",Volume); }
        public void ToggleMute() { Muted=!Muted;PlayerPrefs.SetInt("cape.music.muted",Muted?1:0);PlayerPrefs.Save(); }
        public void SetRiding(bool value) { riding=value; }
        private void Update()
        {
            float target=focused&&!Muted?Volume:0;
            ambient.volume=Mathf.MoveTowards(ambient.volume,riding?target*.22f:target,Time.unscaledDeltaTime*.3f);
            ride.volume=Mathf.MoveTowards(ride.volume,riding?target:0,Time.unscaledDeltaTime*.7f);
        }
        private void OnApplicationFocus(bool focus) { focused=focus;if(!focus){ambient.Pause();ride.Pause();}else{ambient.UnPause();ride.UnPause();} }
        private void OnApplicationPause(bool pause) { focused=!pause;if(pause){ambient.Pause();ride.Pause();}else{ambient.UnPause();ride.UnPause();}PlayerPrefs.Save(); }
    }
}
