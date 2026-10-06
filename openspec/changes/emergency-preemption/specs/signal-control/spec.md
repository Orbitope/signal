## ADDED Requirements

### Requirement: Emergency preemption
A signal controller SHALL request the phase that serves the nearest emergency vehicle on its
incoming links while that vehicle is within the preemption distance of the stop line.

#### Scenario: preemption lets an emergency vehicle through a signal held red
- **WHEN** a policy holds the cross street green and an emergency vehicle is spawned on the other street
- **THEN** the emergency vehicle leaves the network
- **AND** in the same run with the preemption distance set to zero it is still waiting at the stop line

#### Scenario: preemption ends once the emergency vehicle has passed
- **WHEN** the emergency vehicle has left the network
- **THEN** the emergency count is zero, the controller is not preempting, and an outside phase request is honored again

### Requirement: Preemption overrules other requests
While a controller is preempting, it SHALL ignore phase requests from its policy and from outside.

#### Scenario: preemption overrules requests made while it is in force
- **WHEN** an outside caller requests another phase while the controller is preempting
- **THEN** the controller still serves the emergency vehicle's phase

#### Scenario: signals with no emergency vehicle nearby keep following their policy
- **WHEN** an emergency vehicle approaches the first signal of a three-signal arterial
- **THEN** the last signal, with no emergency vehicle on its incoming links, still changes phase as its policy asks

### Requirement: Preemption keeps the safety envelope
Preemption SHALL NOT shorten minimum green, yellow or all-red.

#### Scenario: preemption keeps min-green, yellow and all-red
- **WHEN** emergency vehicles arrive from alternating streets
- **THEN** no green is shorter than the minimum green and every change passes through yellow and all-red

### Requirement: Spawning an emergency vehicle
A simulation SHALL spawn an emergency vehicle on request, ahead of held traffic, and SHALL refuse
quietly when there is no route.

#### Scenario: an emergency vehicle enters ahead of cars held at a backed-up entrance
- **WHEN** cars are held at an entrance and an emergency vehicle is spawned there
- **THEN** the emergency vehicle enters the first link before any held car

#### Scenario: a held emergency vehicle counts toward the average wait
- **WHEN** an emergency vehicle is spawned and has not yet entered its first link
- **THEN** the held-wait total counts one vehicle more than before it was spawned

#### Scenario: spawning an emergency vehicle with no route returns null and changes nothing
- **WHEN** an emergency vehicle is requested between two nodes with no route
- **THEN** the result is null and the emergency count is unchanged

### Requirement: No effect without emergency vehicles
A simulation with no emergency vehicle SHALL behave exactly as before, and one with an emergency
vehicle SHALL NOT be much slower.

#### Scenario: a run with no emergency vehicle has the same state hash as before the change
- **WHEN** the four-way signalized level with symmetric demand of 20 runs 2,000 steps on seed 7 with no policy
- **THEN** its state hash is 1821680719510123714, the value recorded on main at commit 313ea68

#### Scenario: an emergency vehicle in the network keeps at least 75% of the step rate
- **WHEN** the three-signal arterial runs with an emergency vehicle always present, respawned each time one leaves
- **THEN** it runs at 75% or more of the steps per second of the same run with none
