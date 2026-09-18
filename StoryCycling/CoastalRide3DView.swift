import SceneKit
import SwiftUI

struct CoastalRide3DView: UIViewRepresentable {
    let speedKilometersPerHour: Double

    func makeCoordinator() -> Coordinator { Coordinator() }

    func makeUIView(context: Context) -> SCNView {
        let view = SCNView()
        view.scene = context.coordinator.makeScene()
        view.backgroundColor = UIColor(red: 0.08, green: 0.42, blue: 0.62, alpha: 1)
        view.antialiasingMode = .multisampling4X
        view.preferredFramesPerSecond = 60
        view.delegate = context.coordinator
        view.isPlaying = true
        return view
    }

    func updateUIView(_ view: SCNView, context: Context) {
        context.coordinator.speedKilometersPerHour = speedKilometersPerHour
    }

    final class Coordinator: NSObject, SCNSceneRendererDelegate {
        var speedKilometersPerHour: Double = 0
        private var lastFrameTime: TimeInterval = 0
        private var movingSegments: [SCNNode] = []
        private var wheelNodes: [SCNNode] = []

        func makeScene() -> SCNScene {
            let scene = SCNScene()
            scene.background.contents = UIColor(red: 0.08, green: 0.42, blue: 0.62, alpha: 1)

            let camera = SCNNode()
            camera.camera = SCNCamera()
            camera.camera?.fieldOfView = 62
            camera.position = SCNVector3(0, 3.4, 6.8)
            camera.eulerAngles = SCNVector3(-0.26, 0, 0)
            scene.rootNode.addChildNode(camera)

            let sun = SCNNode()
            sun.light = SCNLight()
            sun.light?.type = .directional
            sun.light?.intensity = 1_300
            sun.eulerAngles = SCNVector3(-0.8, -0.45, 0)
            scene.rootNode.addChildNode(sun)

            let fill = SCNNode()
            fill.light = SCNLight()
            fill.light?.type = .ambient
            fill.light?.intensity = 700
            fill.light?.color = UIColor(red: 1, green: 0.72, blue: 0.48, alpha: 1)
            scene.rootNode.addChildNode(fill)

            for index in 0..<6 {
                let segment = makeRoadSegment(index: index)
                segment.position.z = Float(-index * 26)
                scene.rootNode.addChildNode(segment)
                movingSegments.append(segment)
            }

            let player = makePlayerBike()
            player.position = SCNVector3(0, 0.18, 1.1)
            scene.rootNode.addChildNode(player)

            return scene
        }

        func renderer(_ renderer: SCNSceneRenderer, updateAtTime time: TimeInterval) {
            guard lastFrameTime > 0 else {
                lastFrameTime = time
                return
            }
            let delta = min(time - lastFrameTime, 0.1)
            lastFrameTime = time
            let worldSpeed = Float(max(speedKilometersPerHour, 0) * 0.085)
            guard worldSpeed > 0 else { return }

            for segment in movingSegments {
                segment.position.z += worldSpeed * Float(delta)
                if segment.position.z > 24 {
                    segment.position.z -= 156
                }
            }
            for wheel in wheelNodes {
                wheel.eulerAngles.x += worldSpeed * Float(delta) * 5
            }
        }

        private func makeRoadSegment(index: Int) -> SCNNode {
            let segment = SCNNode()
            let ground = SCNBox(width: 34, height: 0.18, length: 26, chamferRadius: 0)
            ground.materials = [material(UIColor(red: 0.75, green: 0.55, blue: 0.24, alpha: 1))]
            let groundNode = SCNNode(geometry: ground)
            groundNode.position.y = -0.15
            segment.addChildNode(groundNode)

            let road = SCNBox(width: 8.2, height: 0.12, length: 26, chamferRadius: 0.06)
            road.materials = [material(UIColor(red: 0.10, green: 0.12, blue: 0.16, alpha: 1))]
            let roadNode = SCNNode(geometry: road)
            segment.addChildNode(roadNode)

            for laneIndex in 0..<5 {
                let marking = SCNBox(width: 0.22, height: 0.03, length: 2.6, chamferRadius: 0.02)
                marking.materials = [material(.white)]
                let marker = SCNNode(geometry: marking)
                marker.position = SCNVector3(0, 0.09, Float(-10 + laneIndex * 5))
                segment.addChildNode(marker)
            }

            for side: Float in [-1, 1] {
                let building = makeBuilding(index: index, side: side)
                segment.addChildNode(building)
                let palm = makePalm()
                palm.position = SCNVector3(side * 7.2, 0, Float(index.isMultiple(of: 2) ? -5 : 5))
                segment.addChildNode(palm)
            }
            return segment
        }

        private func makeBuilding(index: Int, side: Float) -> SCNNode {
            let height = CGFloat(2.4 + Double(index % 3) * 1.35)
            let geometry = SCNBox(width: 3.6, height: height, length: 4.4, chamferRadius: 0.14)
            let palette: [UIColor] = [
                UIColor(red: 0.96, green: 0.35, blue: 0.20, alpha: 1),
                UIColor(red: 0.96, green: 0.72, blue: 0.18, alpha: 1),
                UIColor(red: 0.22, green: 0.60, blue: 0.73, alpha: 1)
            ]
            geometry.materials = [material(palette[index % palette.count])]
            let building = SCNNode(geometry: geometry)
            building.position = SCNVector3(side * 11.2, Float(height / 2), Float(index.isMultiple(of: 2) ? -6 : 6))
            building.eulerAngles.y = side * 0.12
            return building
        }

        private func makePalm() -> SCNNode {
            let palm = SCNNode()
            let trunk = SCNCylinder(radius: 0.16, height: 3.6)
            trunk.materials = [material(UIColor(red: 0.36, green: 0.22, blue: 0.10, alpha: 1))]
            let trunkNode = SCNNode(geometry: trunk)
            trunkNode.position.y = 1.8
            palm.addChildNode(trunkNode)

            for angle in stride(from: 0, through: 300, by: 60) {
                let leaf = SCNBox(width: 0.22, height: 0.08, length: 2.1, chamferRadius: 0.08)
                leaf.materials = [material(UIColor(red: 0.08, green: 0.44, blue: 0.20, alpha: 1))]
                let leafNode = SCNNode(geometry: leaf)
                leafNode.position = SCNVector3(0, 3.65, 0)
                leafNode.eulerAngles = SCNVector3(-0.25, Float(angle) * .pi / 180, 0)
                leafNode.pivot = SCNMatrix4MakeTranslation(0, 0, 1.0)
                palm.addChildNode(leafNode)
            }
            return palm
        }

        private func makePlayerBike() -> SCNNode {
            let bike = SCNNode()
            let frameMaterial = material(UIColor(red: 0.15, green: 0.82, blue: 0.92, alpha: 1))
            for x: Float in [-0.78, 0.78] {
                let wheel = SCNTorus(ringRadius: 0.48, pipeRadius: 0.06)
                wheel.materials = [material(.black)]
                let node = SCNNode(geometry: wheel)
                node.position = SCNVector3(x, 0.52, 0)
                node.eulerAngles.x = .pi / 2
                bike.addChildNode(node)
                wheelNodes.append(node)
            }

            let frame = SCNCylinder(radius: 0.065, height: 1.5)
            frame.materials = [frameMaterial]
            let frameNode = SCNNode(geometry: frame)
            frameNode.position = SCNVector3(0, 0.83, 0)
            frameNode.eulerAngles.z = .pi / 2
            bike.addChildNode(frameNode)

            let rider = SCNCapsule(capRadius: 0.19, height: 1.05)
            rider.materials = [material(UIColor(red: 0.94, green: 0.48, blue: 0.22, alpha: 1))]
            let riderNode = SCNNode(geometry: rider)
            riderNode.position = SCNVector3(0, 1.34, -0.04)
            riderNode.eulerAngles.z = -0.18
            bike.addChildNode(riderNode)
            return bike
        }

        private func material(_ color: UIColor) -> SCNMaterial {
            let material = SCNMaterial()
            material.diffuse.contents = color
            material.lightingModel = .physicallyBased
            material.roughness.contents = 0.72
            return material
        }
    }
}
