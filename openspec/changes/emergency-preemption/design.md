# Design: Emergency vehicle preemption

## Decisions
Decided by Matthew on 2026-10-06.

- **Preemption lives in the controller, not in a policy.** The controller owns the safety envelope
  and is the one place every phase request passes through.
- **Preemption wins over everything.** `SignalController.RequestPhase` is called from three places:
  the controller's own tick, `AgentView.Act` (every training action) and the game's tap handler. While
  the controller is preempting, requests from outside are ignored.
- **Policies do not see emergency vehicles.** The weight loader rejects a file whose observation
  size differs from `ObsSchema.Size`, and the exporter hard-codes 233. Adding a feature would
  invalidate every trained policy.
- **Minimum green is kept.** The controller looks 250 metres ahead and checks every tick, not only
  when its decision timer fires, so the green can arrive in time without shortening anything.
- **An emergency vehicle enters at the front.** It has its own entry queue per origin, released
  before the ordinary one.
- **The controller only looks when the simulation says an emergency vehicle exists.** The simulation
  keeps a count. At zero, a tick costs one integer comparison more than today.

## How the controller picks a phase
- It looks only at vehicles on its own incoming links. A link shorter than the preemption distance
  limits how far it sees.
- The nearest emergency vehicle to the stop line wins. A tie goes to the lower link id.
- The phase is the first one that serves the vehicle's movement as protected, else the first that
  serves it as permissive. If none does, or the vehicle has no next link, there is no preemption.

## Limits accepted
- The emergency vehicle drives like any other and queues behind traffic. Preemption clears the
  signal, not the road.
- While one emergency vehicle exists anywhere, every signal scans its incoming links each tick.
