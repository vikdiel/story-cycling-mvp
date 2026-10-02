namespace StoryCycling
{
    // Gemeinsame Sicht des Menüs/HUDs auf eine Fahrt: Cape-Crown-Rundkurse (CapeCrownRideController) und GPX-Welten (GpxRideController).
    // Geschwindigkeit/Leistung/Puls kommen in beiden Fällen aus CapeCrownDevices (Trainer, Brustgurt oder Demo).
    public interface IRideSession
    {
        bool Started { get; }
        bool IsPaused { get; }
        bool CanStart { get; }
        bool IsDemo { get; }
        string PauseReason { get; }
        string RouteLabel { get; }
        string RouteDescription { get; }
        bool HasRouteList { get; }           // Routenwahl (Cape-Crown-Szenen) anbieten
        float TotalMetres { get; }
        float SpeedKph { get; }              // Tempo im Spiel (Fahrphysik aus Leistung bzw. Trainer-/Demo-Tempo)
        float RouteDistance { get; }
        float RouteLength { get; }
        int CompletedLaps { get; }
        float CurrentGrade { get; }          // Steigung an der Fahrerposition (0.05 = 5 %)
        float HeightAhead(float metres);     // Höhe der Fahrlinie (m, Welt-Y) so viele Meter voraus
        bool TryStart();
        void Pause(string reason = "Pausiert");
        bool Resume();
        void EndRide();
        void StartDemo();
        void StopDemo();
        void SetDemoSpeed(float kph);
        void ResetDemoSpeed();
    }
}
