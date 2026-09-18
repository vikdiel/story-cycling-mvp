import SwiftUI

struct ZwiftClickSheet: View {
    @ObservedObject var click: ZwiftClickManager

    var body: some View {
        NavigationStack {
            List {
                Section("Verbindung") {
                    Label(click.state.label, systemImage: "switch.2")
                    Button("Zwift Click suchen") {
                        click.connect()
                    }
                    Button("Trennen", role: .destructive) {
                        click.disconnect()
                    }
                }

                Section("Kopplung") {
                    Text("Zum Koppeln eine +/- Taste drücken, bis der Click blau pulsiert")
                        .foregroundStyle(.secondary)
                }
            }
            .navigationTitle("Zwift Click")
            .onAppear { click.connect() }
        }
    }
}
