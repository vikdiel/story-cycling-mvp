import SwiftUI

struct TrainerConnectionSheet: View {
    @ObservedObject var trainer: TrainerConnectionManager

    var body: some View {
        NavigationStack {
            List {
                Section("Verbindungsstatus") {
                    Label(trainer.state.label, systemImage: trainer.state.isConnected ? "checkmark.circle.fill" : "dot.radiowaves.left.and.right")
                        .foregroundStyle(trainer.state.isConnected ? .green : .primary)

                    if trainer.state.isConnected {
                        Button("Trainer trennen", role: .destructive) {
                            trainer.disconnect()
                        }
                    } else {
                        Button("Nach Trainern suchen") {
                            trainer.startScan()
                        }
                    }
                }

                Section("Gefundene Trainer") {
                    if trainer.trainers.isEmpty {
                        Text("Schalte den KICKR Core ein und stelle sicher, dass er nicht parallel mit Zwift oder Wahoo SYSTM verbunden ist")
                            .foregroundStyle(.secondary)
                    }

                    ForEach(trainer.trainers) { candidate in
                        Button {
                            trainer.connect(to: candidate)
                        } label: {
                            HStack {
                                Image(systemName: "bicycle")
                                Text(candidate.displayName)
                                Spacer()
                                Image(systemName: "chevron.right")
                                    .font(.caption)
                                    .foregroundStyle(.tertiary)
                            }
                        }
                        .disabled(trainer.state.isConnected)
                    }
                }

                Section("Widerstand") {
                    if let range = trainer.resistanceRange {
                        Text("Aktuell: \(trainer.currentResistance?.formatted(.number.precision(.fractionLength(1))) ?? "—")")
                        HStack {
                            Button("Leichter") {
                                trainer.setResistance((trainer.currentResistance ?? range.lowerBound) - 1)
                            }
                            Spacer()
                            Button("Schwerer") {
                                trainer.setResistance((trainer.currentResistance ?? range.lowerBound) + 1)
                            }
                        }
                        Text("Verfügbarer Bereich: \(range.lowerBound.formatted(.number.precision(.fractionLength(1))))–\(range.upperBound.formatted(.number.precision(.fractionLength(1))))")
                            .font(.footnote)
                            .foregroundStyle(.secondary)
                    } else {
                        Text("Widerstand wird verfügbar, sobald der Trainer die unterstützten FTMS-Grenzen meldet")
                            .foregroundStyle(.secondary)
                    }
                    if let error = trainer.resistanceError {
                        Text(error).foregroundStyle(.red)
                    }
                }
            }
            .navigationTitle("KICKR Core")
            .onAppear { trainer.startScan() }
        }
    }
}
