import SwiftUI

@main
struct PhlanMicIOSClientApp: App {
    @Environment(\.scenePhase) private var scenePhase
    @StateObject private var model = AppModel()

    var body: some Scene {
        WindowGroup {
            ContentView(model: model)
                .onChange(of: scenePhase) {
                    model.handleScenePhaseChange(scenePhase)
                }
        }
    }
}
