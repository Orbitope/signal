## ADDED Requirements

### Requirement: Emergency preemption
A signal controller SHALL request the phase that serves an approaching emergency vehicle while
that vehicle is within the preemption distance of the stop line.

#### Scenario: preemption gives an emergency vehicle green before it reaches the stop line
- **WHEN** an emergency vehicle is spawned toward a signal whose current phase does not serve it
- **THEN** the phase serving its movement is green before the vehicle reaches the end of its link

#### Scenario: preemption ends once the emergency vehicle has passed
- **WHEN** the emergency vehicle has left the network
- **THEN** the controller is no longer preempting and its policy decides the next phase

### Requirement: Preemption keeps the safety envelope
Preemption SHALL NOT shorten minimum green, yellow or all-red.

#### Scenario: preemption keeps min-green, yellow and all-red
- **WHEN** emergency vehicles arrive from alternating approaches
- **THEN** no green is shorter than the minimum green and every change passes through yellow and all-red

### Requirement: No effect without emergency vehicles
A simulation with no emergency vehicle SHALL behave exactly as before.

#### Scenario: without an emergency vehicle the policy decides as before
- **WHEN** a level runs for 2,000 steps with no emergency vehicle
- **THEN** its state hash equals the hash of the same run on the code before this change
