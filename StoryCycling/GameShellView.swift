import SwiftUI

struct GameShellView: View {
    @State private var rideIsRunning = false
    @State private var progress = 0.18
    @State private var showsTrainerSheet = false
    @StateObject private var trainer = TrainerConnectionManager()

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
            }
            .padding(40)
            .foregroundStyle(.white)
        }
        .sheet(isPresented: $showsTrainerSheet) {
            TrainerConnectionSheet(trainer: trainer)
        }
    }
}

#Preview {
    GameShellView()
}
