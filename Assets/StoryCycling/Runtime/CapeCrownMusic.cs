using UnityEngine;

namespace StoryCycling
{
    public sealed class CapeCrownMusic : MonoBehaviour
    {
        private AudioSource source;
        private bool focused=true;
        public float Volume { get; private set; }
        public bool Muted { get; private set; }
        private void Awake()
        {
            Volume=PlayerPrefs.GetFloat("cape.music.volume",.32f);
            Muted=PlayerPrefs.GetInt("cape.music.muted",0)==1;
            source=gameObject.AddComponent<AudioSource>();
            source.clip=Resources.Load<AudioClip>("CampsBayEvening");
            source.loop=true;source.spatialBlend=0;source.volume=0;source.playOnAwake=false;
            if(source.clip!=null)source.Play();
        }
        public void SetVolume(float value) { Volume=Mathf.Clamp01(value);PlayerPrefs.SetFloat("cape.music.volume",Volume); }
        public void ToggleMute() { Muted=!Muted;PlayerPrefs.SetInt("cape.music.muted",Muted?1:0);PlayerPrefs.Save(); }
        private void Update() { source.volume=Mathf.MoveTowards(source.volume,focused&&!Muted?Volume:0,Time.unscaledDeltaTime*.4f); }
        private void OnApplicationFocus(bool focus) { focused=focus;if(!focus)source.Pause();else source.UnPause(); }
        private void OnApplicationPause(bool pause) { focused=!pause;if(pause)source.Pause();else source.UnPause();PlayerPrefs.Save(); }
    }
}
