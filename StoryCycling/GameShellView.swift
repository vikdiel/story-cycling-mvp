import SwiftUI

struct GameShellView: View {
    @State private var rideIsRunning = false
    @State private var progress = 0.18
    @State private var showsTrainerSheet = false
    @State private var showsHeartRateSheet = false
    @State private var showsClickSheet = false
    @State private var virtualGear = 12
    @StateObject private var trainer = TrainerConnectionManager()
    @StateObject private var heartRate = HeartRateMonitorManager()
    @StateObject private var click = ZwiftClickManager()

    var body: some View {
        ZStack {
            LinearGradient(
                colors: [Color(red: 0.03, green: 0.13, blue: 0.19), Color(red: 0.95, green: 0.36, blue: 0.17)],
                startPoint: .top,
                endPoint: .bottom
            )
            .ignoresSafeArea()

            VStack(alignment: .leading, spacing: 24) {
                HStack {
                    VStack(alignment: .leading, spacing: 4) {
                        Text("STORY CYCLING")
                            .font(.caption.weight(.bold))
                            .tracking(2)
                        Text("Winter in Südafrika")
                            .font(.largeTitle.bold())
                    }
                    Spacer()
                    Button {
                        showsTrainerSheet = true
                    } label: {
                        Label(trainer.state.label, systemImage: trainer.state.isConnected ? "checkmark.circle.fill" : "dot.radiowaves.left.and.right")
                            .font(.subheadline.weight(.medium))
                            .padding(12)
                            .background(.thinMaterial, in: Capsule())
                    }
                    .buttonStyle(.plain)

                    Button {
                        showsHeartRateSheet = true
                    } label: {
                        Label(heartRate.beatsPerMinute.map { "\($0) BPM" } ?? "Puls", systemImage: "heart.fill")
                            .font(.subheadline.weight(.medium))
                            .padding(12)
                            .background(.thinMaterial, in: Capsule())
                    }
                    .buttonStyle(.plain)

                    Button { showsClickSheet = true } label: {
                        Label("Click", systemImage: "switch.2")
                            .font(.subheadline.weight(.medium)).padding(12).background(.thinMaterial, in: Capsule())
                    }.buttonStyle(.plain)
                }

                Spacer()

                VStack(alignment: .leading, spacing: 14) {
                    Text("KAPITEL 1 · ANKUNFT")
                        .font(.headline)
                    Text("Cape Crown — Prolog")
                        .font(.system(size: 38, weight: .bold, design: .rounded))
                    Text("Ein Winter. Fünf Rennen. Der erste Schritt zur Weltmeisterschaft beginnt hier.")
                        .font(.title3)
                        .foregroundStyle(.white.opacity(0.84))
                        .frame(maxWidth: 620, alignment: .leading)

                    ProgressView(value: progress)
                        .tint(.white)
                        .accessibilityLabel("Streckenfortschritt")
                        .accessibilityValue("\(Int(progress * 100)) Prozent")
                    Text("Demo-Strecke · \(Int(progress * 10)) von 10 km")
                        .font(.subheadline.weight(.medium))
                }
                .padding(28)
                .background(.black.opacity(0.24), in: RoundedRectangle(cornerRadius: 28))

                RideSceneView(speedKilometersPerHour: trainer.speedKilometersPerHour)
                    .frame(height: 220)
                Text("\(trainer.speedKilometersPerHour.formatted(.number.precision(.fractionLength(1)))) km/h")
                    .font(.title3.monospacedDigit().bold())

                HStack(spacing: 16) {
                    Button(rideIsRunning ? "Demo pausieren" : "Demo-Fahrt starten") {
                        rideIsRunning.toggle()
                        if rideIsRunning {
                            withAnimation(.easeInOut(duration: 1.2)) {
                                progress = min(progress + 0.12, 1)
                            }
                        }
                    }
                    .buttonStyle(.borderedProminent)
                    .tint(.white)
                    .foregroundStyle(Color(red: 0.08, green: 0.15, blue: 0.2))
                    .font(.title3.weight(.bold))

                    Text(trainer.state.isConnected ? "KICKR bereit für die erste Fahrt" : "KICKR Core über die Statusanzeige verbinden")
                        .font(.subheadline)
                        .foregroundStyle(.white.opacity(0.8))
                }

                if trainer.resistanceRange != nil {
                    HStack(spacing: 18) {
                        Button { changeGear(by: -1) } label: {
                            Image(systemName: "minus.circle.fill")
                        }
                        .font(.title)
                        .accessibilityLabel("Gang leichter")

                        VStack(spacing: 2) {
                            Text("VIRTUELLER GANG")
                                .font(.caption2.weight(.bold))
                                .tracking(1)
                            Text("\(virtualGear) / 24")
                                .font(.title2.bold())
                        }

                        Button { changeGear(by: 1) } label: {
                            Image(systemName: "plus.circle.fill")
                        }
                        .font(.title)
                        .accessibilityLabel("Gang schwerer")
                    }
                    .padding(.horizontal, 18)
                    .padding(.vertical, 10)
                    .background(.black.opacity(0.24), in: Capsule())
                }
            }
            .padding(40)
            .foregroundStyle(.white)
        }
        .sheet(isPresented: $showsTrainerSheet) {
            TrainerConnectionSheet(trainer: trainer)
        }
        .sheet(isPresented: $showsHeartRateSheet) {
            HeartRateMonitorSheet(monitor: heartRate)
        }
        .sheet(isPresented: $showsClickSheet) {
            ZwiftClickSheet(click: click)
        }
        .onChange(of: click.shiftEventCounter) { _, _ in
            if let direction = click.lastDirection { changeGear(by: direction == .up ? 1 : -1) }
        }
    }

    private func changeGear(by change: Int) {
        let nextGear = min(max(virtualGear + change, 1), 24)
        guard nextGear != virtualGear else { return }
        virtualGear = nextGear

        guard let range = trainer.resistanceRange else { return }
        let fraction = Double(nextGear - 1) / 23
        trainer.setResistance(range.lowerBound + (range.upperBound - range.lowerBound) * fraction)
    }
}

#Preview {
    GameShellView()
}
