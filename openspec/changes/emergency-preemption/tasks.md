## 1. Vehicles
- [ ] 1.1 Add `Vehicle.IsEmergency`, false by default
- [ ] 1.2 Add `DemandSource.SpawnEmergency`, which routes an emergency vehicle from an origin to a destination and puts it in the entry queue
- [ ] 1.3 Add `Simulation.SpawnEmergency`, which calls the demand source
- [ ] 1.4 Add `Simulation.EmergencyCount`, raised in `Simulation.OnVehicleSpawned` and lowered where a vehicle leaves in `Simulation.TryTransfer`

## 2. Controller
- [ ] 2.1 Add `SignalController.PreemptDistance`, 150 metres by default
- [ ] 2.2 Add `SignalController.PreemptPhase`, which returns the phase serving the nearest emergency vehicle within the distance, or -1
- [ ] 2.3 Add `SignalController.Preempting`, true while a preemption request is in force
- [ ] 2.4 Change `SignalController.Tick` to request the preempt phase in place of asking the policy when `Simulation.EmergencyCount` is above zero

## 3. Tests
- [ ] 3.1 Add the test "preemption gives an emergency vehicle green before it reaches the stop line" to `Signal.Tests/Program.cs`
- [ ] 3.2 Add the test "preemption ends once the emergency vehicle has passed"
- [ ] 3.3 Add the test "preemption keeps min-green, yellow and all-red"
- [ ] 3.4 Add the test "without an emergency vehicle the policy decides as before"
