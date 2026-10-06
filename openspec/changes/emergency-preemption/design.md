# Design: Emergency vehicle preemption

## Decisions
- **Preemption lives in the controller, not in a policy.** The controller already owns the safety
  envelope. A policy that could be overruled by another policy would need an ordering rule; the
  controller overruling its policy needs none. Cost: a learned policy cannot plan around an
  approaching emergency vehicle.
- **Policies do not see emergency vehicles.** Adding a feature to the observation vector changes
  its size, and every trained weight file is tied to that size.
- **The controller only looks when the simulation says an emergency vehicle exists.** The
  simulation keeps a count. With the count at zero, a tick costs one integer comparison more than
  today.
- **The emergency vehicle drives like any other.** It follows the same car-following model and
  queues behind traffic. Preemption clears the signal, not the road.

## Open questions
- Should preemption shorten minimum green? Real controllers often do. This change does not.
