import Combine
import SwiftUI

struct RideSceneView: View {
    let speedKilometersPerHour: Double
    @State private var roadOffset: CGFloat = 0
    private let ticker = Timer.publish(every: 0.05, on: .main, in: .common).autoconnect()
    var body: some View {
        GeometryReader { geometry in
            ZStack {
                LinearGradient(colors: [.cyan.opacity(0.9), .orange.opacity(0.7)], startPoint: .top, endPoint: .bottom)
                Path { path in path.move(to: .zero); path.addLine(to: CGPoint(x: geometry.size.width * 0.35, y: geometry.size.height * 0.45)); path.addLine(to: CGPoint(x: geometry.size.width * 0.7, y: geometry.size.height * 0.35)); path.addLine(to: CGPoint(x: geometry.size.width, y: geometry.size.height * 0.52)); path.addLine(to: CGPoint(x: geometry.size.width, y: geometry.size.height)); path.addLine(to: .zero) }.fill(.green.opacity(0.75))
                Rectangle().fill(.black.opacity(0.72)).frame(height: geometry.size.height * 0.34).offset(y: geometry.size.height * 0.33)
                ForEach(0..<8, id: \.self) { index in
                    Capsule().fill(.white).frame(width: 58, height: 7).offset(x: CGFloat(index) * 120 - roadOffset, y: geometry.size.height * 0.33)
                }
                Image(systemName: "bicycle").font(.system(size: 70, weight: .bold)).foregroundStyle(.white).rotationEffect(.degrees(speedKilometersPerHour > 0 ? -2 : 0)).offset(y: geometry.size.height * 0.20)
            }
        }
        .clipShape(RoundedRectangle(cornerRadius: 28))
        .onReceive(ticker) { _ in roadOffset = speedKilometersPerHour > 0 ? (roadOffset + CGFloat(speedKilometersPerHour) * 0.9).truncatingRemainder(dividingBy: 120) : roadOffset }
        .accessibilityLabel("2D Fahrtansicht, aktuelle Geschwindigkeit \(speedKilometersPerHour.formatted(.number.precision(.fractionLength(1)))) Kilometer pro Stunde")
    }
}
