import SwiftUI

struct HeartRateMonitorSheet: View {
    @ObservedObject var monitor: HeartRateMonitorManager

    var body: some View {
        NavigationStack {
            List {
                Section("Status") {
                    Label(monitor.state.label, systemImage: "heart.fill")
                        .foregroundStyle(.red)
                    if let bpm = monitor.beatsPerMinute {
                        Text("\(bpm) BPM").font(.title2.bold())
                    }
                    Button(monitor.beatsPerMinute == nil ? "Brustgurt suchen" : "Brustgurt trennen", role: monitor.beatsPerMinute == nil ? nil : .destructive) {
                        if monitor.beatsPerMinute == nil { monitor.startScan() } else { monitor.disconnect() }
                    }
                }

                Section("Gefundene Brustgurte") {
                    if monitor.monitors.isEmpty {
                        Text("Brustgurt anfeuchten und anlegen, damit er aufwacht")
                            .foregroundStyle(.secondary)
                    }
                    ForEach(monitor.monitors) { candidate in
                        Button(candidate.displayName) { monitor.connect(to: candidate) }
                    }
                }
            }
            .navigationTitle("Herzfrequenz")
            .onAppear { monitor.startScan() }
        }
    }
}
