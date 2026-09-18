using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Scripting;

namespace StoryCycling
{
    // Shared by HUD and ride. Each metric expires independently (FTMS packets can be split).
    public sealed class CapeCrownDevices : MonoBehaviour
    {
        [Serializable] public class Event { public string type, role, id, name, state, data, characteristic; }
        [Serializable] public class Candidate { public string id, name, role; }
        public readonly List<Candidate> Candidates = new List<Candidate>();
        public string TrainerState { get; private set; } = "Nicht verbunden";
        public string HeartState { get; private set; } = "Nicht verbunden";
        public string TrainerName { get; private set; } = "Wahoo KICKR Core";
        public string HeartName { get; private set; } = "Bluetooth-Brustgurt";
        public bool TrainerConnected { get; private set; }
        public bool HeartConnected { get; private set; }
        private float speed, watts, cadence, heart, speedAt = -100, powerAt = -100, cadenceAt = -100, heartAt = -100;
        public bool FreshSpeed => TrainerConnected && Time.realtimeSinceStartup - speedAt < 3;
        public bool FreshPower => TrainerConnected && Time.realtimeSinceStartup - powerAt < 3;
        public bool FreshCadence => TrainerConnected && Time.realtimeSinceStartup - cadenceAt < 3;
        public bool FreshHeart => HeartConnected && Time.realtimeSinceStartup - heartAt < 5;
        public float Speed => FreshSpeed ? speed : 0;
        public float Watts => watts;
        public float Cadence => cadence;
        public float Heart => heart;
        public int Revision { get; private set; }
        public bool IsTestFeed { get; private set; }
#if UNITY_IOS && !UNITY_EDITOR
        [DllImport("__Internal")] private static extern void CCDevicesInit(string target);
        [DllImport("__Internal")] private static extern void CCDevicesScan(string role);
        [DllImport("__Internal")] private static extern void CCDevicesConnect(string id, string role);
        [DllImport("__Internal")] private static extern void CCDevicesDisconnect(string role);
        [DllImport("__Internal")] private static extern void CCDevicesShutdown();
#endif
        private void Awake() { gameObject.name = "Cape Crown Devices"; }
        public void Scan(string role)
        {
            if(role=="trainer"?TrainerConnected:HeartConnected)return;
            Candidates.RemoveAll(x => x.role == role); Revision++;
#if UNITY_IOS && !UNITY_EDITOR
            CCDevicesInit(gameObject.name); CCDevicesScan(role);
#else
            SetState(role,"Bluetooth auf dem iPad verfügbar");
#endif
        }
        public void Connect(Candidate device)
        {
#if UNITY_IOS && !UNITY_EDITOR
            CCDevicesInit(gameObject.name); CCDevicesConnect(device.id, device.role);
#endif
        }
        public void Disconnect(string role)
        {
#if UNITY_IOS && !UNITY_EDITOR
            CCDevicesDisconnect(role);
#endif
            SetState(role,"Getrennt");
        }
        private void SetState(string role, string state)
        {
            if (role == "trainer") { TrainerState = state; TrainerConnected = state == "Bereit"; if (!TrainerConnected) speedAt = powerAt = cadenceAt = -100; }
            else { HeartState = state; HeartConnected = state == "Bereit"; if (!HeartConnected) heartAt = -100; }
            Revision++;
        }
        [Preserve] public void OnBluetoothEvent(string json)
        {
            try
            {
                Event e = JsonUtility.FromJson<Event>(json);
                if (e == null) return;
                if (e.type == "candidate")
                {
                    if (!Candidates.Exists(x => x.id == e.id && x.role == e.role))
                    { Candidates.Add(new Candidate { id=e.id, name=e.name, role=e.role }); Revision++; }
                }
                else if (e.type == "state")
                {
                    SetState(e.role,e.state);
                    if (!string.IsNullOrEmpty(e.name)) { if(e.role=="trainer") TrainerName=e.name; else HeartName=e.name; }
                }
                else if (e.type == "data") ApplyPacket(e.characteristic, Convert.FromBase64String(e.data));
            }
            catch (Exception e) { Debug.LogWarning("BLE packet rejected: " + e.GetType().Name); }
        }
        public void ApplyPacket(string characteristic, byte[] bytes)
        {
            float now = Time.realtimeSinceStartup;
            if (characteristic == "2AD2" && TrainerConnected && CapeCrownTelemetry.TryBike(bytes,out var data))
            {
                if(data.hasSpeed) { speed=data.speed; speedAt=now; }
                if(data.hasPower) { watts=data.power; powerAt=now; }
                if(data.hasCadence) { cadence=data.cadence; cadenceAt=now; }
            }
            if(characteristic == "2A37" && HeartConnected && CapeCrownTelemetry.TryHeart(bytes,out int bpm)) { heart=bpm; heartAt=now; }
        }
        private void OnApplicationPause(bool paused) { if(paused) speedAt=powerAt=cadenceAt=heartAt=-100; }
        private void OnDestroy()
        {
#if UNITY_IOS && !UNITY_EDITOR
            CCDevicesShutdown();
#endif
        }
#if UNITY_EDITOR
        // Not compiled into iPad builds. Test ride never counts toward training progress.
        public void ReviewFeed(float kph, int w=160, int bpm=118)
        {
            IsTestFeed=true; TrainerConnected=HeartConnected=true; TrainerState=HeartState="Bereit";
            TrainerName="EDITOR TEST"; speed=kph; watts=w; cadence=kph>0?78:0; heart=bpm;
            speedAt=powerAt=cadenceAt=heartAt=Time.realtimeSinceStartup;
        }
        public void ReviewExpire() { speedAt=powerAt=cadenceAt=heartAt=-100; }
#endif

        // Demo ride for testing without a trainer. Cross-platform and clearly flagged;
        // the ride controller never counts demo distance toward training progress.
        public void StartDemoFeed()
        {
            IsTestFeed = true;
            TrainerConnected = HeartConnected = true;
            TrainerState = HeartState = "Bereit";
            TrainerName = "DEMO-FAHRT"; HeartName = "Simuliert";
            speed = 0; watts = 0; cadence = 0; heart = 120;
            speedAt = powerAt = cadenceAt = heartAt = Time.realtimeSinceStartup;
            Revision++;
        }
        public void StopDemoFeed()
        {
            IsTestFeed = false;
            TrainerConnected = HeartConnected = false;
            TrainerState = HeartState = "Nicht verbunden";
            speedAt = powerAt = cadenceAt = heartAt = -100;
            Revision++;
        }
        public void TickDemo(float kph)
        {
            if (!IsTestFeed) return;
            float now = Time.realtimeSinceStartup;
            speed = Mathf.Max(0, kph);
            watts = Mathf.RoundToInt(Mathf.Lerp(90f, 220f, Mathf.Clamp01(kph / 40f)));
            cadence = Mathf.Lerp(50f, 90f, Mathf.Clamp01(kph / 40f));
            heart = 128;
            speedAt = powerAt = cadenceAt = heartAt = now;
        }
    }

    public static class CapeCrownTelemetry
    {
        public struct Bike { public bool hasSpeed,hasPower,hasCadence; public float speed,power,cadence; }
        public static bool TryBike(byte[] b,out Bike data)
        {
            data=default; if(b==null || b.Length<2) return false;
            int flags=U16(b,0), p=2;
            var result=new Bike();
            if((flags&1)==0) { if(p+2>b.Length)return false; result.hasSpeed=true; result.speed=U16(b,p)/100f; p+=2; }
            int[] sizes={2,2,2,3,2,2,2,5,1,1,2,2};
            for(int bit=1;bit<=12;bit++)
            {
                if((flags&(1<<bit))==0)continue;
                int size=sizes[bit-1]; if(p+size>b.Length)return false;
                if(bit==2) { result.hasCadence=true; result.cadence=U16(b,p)/2f; }
                if(bit==6) { result.hasPower=true; result.power=Math.Max(0,(int)(short)U16(b,p)); }
                p+=size;
            }
            if(result.speed>120 || result.cadence>300 || result.power>4000)return false;
            data=result;return true;
        }
        public static bool TryHeart(byte[] b,out int bpm)
        {
            bpm=0; if(b==null || b.Length<2)return false;
            bool wide=(b[0]&1)!=0; if(wide&&b.Length<3)return false;
            // If contact detection is supported but no contact, do not show a stale/invalid pulse.
            if((b[0]&4)!=0 && (b[0]&2)==0)return false;
            bpm=wide?U16(b,1):b[1]; return bpm>0&&bpm<=250;
        }
        private static int U16(byte[] b,int p)=>b[p]|b[p+1]<<8;
    }
}
