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
        // Zwift Click (zwei Tasten): Shift(true) = schwerer / Plus, Shift(false) = leichter / Minus
        public string ClickState { get; private set; } = "Nicht verbunden";
        public string ClickName { get; private set; } = "Zwift Click";
        public bool ClickConnected { get; private set; }
        public event Action<bool> Shift;
        private bool clickPlus, clickMinus;
        public const string ClickAsyncUuid = "00000002-19CA-4651-86E5-FA29DCDD09D1";
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
        // FTMS-Steuerung (Widerstand): verfügbar, sobald der Trainer 'Request Control' bestätigt hat
        public bool ControlReady { get; private set; }
        public string ControlState { get; private set; } = "—";
        public string LastCommand { get; private set; } = "";
        public bool IsTestFeed { get; private set; }
#if UNITY_IOS && !UNITY_EDITOR
        [DllImport("__Internal")] private static extern void CCDevicesInit(string target);
        [DllImport("__Internal")] private static extern void CCDevicesScan(string role);
        [DllImport("__Internal")] private static extern void CCDevicesConnect(string id, string role);
        [DllImport("__Internal")] private static extern void CCDevicesDisconnect(string role);
        [DllImport("__Internal")] private static extern void CCDevicesShutdown();
        [DllImport("__Internal")] private static extern void CCDevicesTrainerCommand(string base64);
#endif
        private void Awake() { gameObject.name = "Cape Crown Devices"; }
        public void Scan(string role)
        {
            if(role=="trainer"?TrainerConnected:role=="click"?ClickConnected:HeartConnected)return;
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
            if (role == "trainer") { TrainerState = state; TrainerConnected = state == "Bereit"; if (!TrainerConnected) { speedAt = powerAt = cadenceAt = -100; ControlReady = false; ControlState = "—"; } }
            else if (role == "click") { ClickState = state; ClickConnected = state == "Bereit"; clickPlus = clickMinus = false; }
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
                    if (!string.IsNullOrEmpty(e.name)) { if(e.role=="trainer") TrainerName=e.name; else if(e.role=="click") ClickName=e.name; else HeartName=e.name; }
                }
                else if (e.type == "control") { ControlState = e.state; ControlReady = false; Revision++; }
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
            if(characteristic == "2AD9" && bytes != null && bytes.Length >= 3 && bytes[0] == 0x80) ApplyControlResponse(bytes[1], bytes[2]);
            if(characteristic == ClickAsyncUuid && ClickConnected && CapeCrownTelemetry.TryClick(bytes, out bool plus, out bool minus))
            {
                if (plus && !clickPlus) Shift?.Invoke(true);                   // nur beim Drücken (Flanke), nicht beim Halten/Loslassen
                if (minus && !clickMinus) Shift?.Invoke(false);
                clickPlus = plus; clickMinus = minus;
            }
        }
        // Antwort auf einen Steuerbefehl: 0x80, Befehl, Ergebnis (1 = ok, 2 = nicht unterstützt, 3 = ungültiger Wert, 4 = fehlgeschlagen, 5 = Steuerung nicht erlaubt)
        private void ApplyControlResponse(byte op, byte result)
        {
            if (result == 1) { ControlReady = true; if (ControlState != "Widerstand aktiv") { ControlState = "Widerstand aktiv"; Revision++; } return; }
            string what = op == 0x11 ? "Steigung" : op == 0x05 ? "Wattvorgabe" : op == 0x00 ? "Steuerung" : "Befehl 0x" + op.ToString("X2");
            ControlState = what + (result == 2 ? " nicht unterstützt" : result == 3 ? ": ungültiger Wert" : result == 5 ? " nicht erlaubt (andere App verbunden?)" : " fehlgeschlagen");
            if (op == 0x00) ControlReady = false;
            Revision++;
        }

        // Simulationsmodus (FTMS 0x11): Steigung in %, Rollwiderstand Crr, Luftwiderstand Cw (kg/m = 0,5 · Luftdichte · CdA), Wind m/s
        public void SendSimulation(float gradePercent, float crr, float cw, float windMps = 0f)
        {
            int wind = Mathf.Clamp(Mathf.RoundToInt(windMps * 1000f), short.MinValue, short.MaxValue), grade = Mathf.Clamp(Mathf.RoundToInt(gradePercent * 100f), short.MinValue, short.MaxValue);
            var b = new byte[] { 0x11, (byte)(wind & 0xFF), (byte)((wind >> 8) & 0xFF), (byte)(grade & 0xFF), (byte)((grade >> 8) & 0xFF),
                                 (byte)Mathf.Clamp(Mathf.RoundToInt(crr * 10000f), 0, 255), (byte)Mathf.Clamp(Mathf.RoundToInt(cw * 100f), 0, 255) };
            LastCommand = $"Steigung {gradePercent:0.0} %"; Send(b);
        }
        // ERG (FTMS 0x05): Zielleistung in W
        public void SendTargetPower(int watts)
        {
            int w = Mathf.Clamp(watts, 0, 2000);
            LastCommand = $"ERG {w} W"; Send(new byte[] { 0x05, (byte)(w & 0xFF), (byte)((w >> 8) & 0xFF) });
        }
        private void Send(byte[] command)
        {
            if (IsTestFeed || !TrainerConnected) return;
#if UNITY_IOS && !UNITY_EDITOR
            CCDevicesTrainerCommand(Convert.ToBase64String(command));
#endif
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
        // Zwift Click: Nachricht 0x37 (Tastenstatus), danach Feld/Wert-Paare: 0x08 = Plus, 0x10 = Minus; Wert 0 = gedrückt, 1 = los
        public static bool TryClick(byte[] b,out bool plus,out bool minus)
        {
            plus=minus=false; if(b==null || b.Length<3 || b[0]!=0x37) return false;
            for(int i=1;i+1<b.Length;i+=2) { if(b[i]==0x08) plus=b[i+1]==0; else if(b[i]==0x10) minus=b[i+1]==0; }
            return true;
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
