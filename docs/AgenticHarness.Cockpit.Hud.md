> Historical design conversation. It is not a current goal or status authority; see [Agentica Product Status And Goal Xref](Agentica.ProductStatus.md).

Show more
Okay, I'm deliberately starting this conversation in a new
thread instead of picking up an existing thread because I
want to avoid as much existing gravity as possible beyond
what you actually have in shared context. So I've been
working on agentica and harnesses to test agentica. So we
have the hex quest harness that is basically codebreaker.
The agent basically tries to edit an encoded file that it
can't see the actual values for. It has to use pattern
matching to infer the values, alter an encoded value to get
the appropriate response. Then we have the maze quest
harness, which essentially is a set of tools that let the
Okay, I'm deliberately starting this conversation in a new
thread instead of picking up an existing thread because I
want to avoid as much existing gravity as possible beyond
what you actually have in shared context. So I've been
working on agentica and harnesses to test agentica. So we
have the hex quest harness that is basically codebreaker.
The agent basically tries to edit an encoded file that it
can't see the actual values for. It has to use pattern
matching to infer the values, alter an encoded value to get
the appropriate response. Then we have the maze quest
harness, which essentially is a set of tools that let the
I’ve got enough to engage meaningfully—what you laid out already exposes the real
problem space. If anything, reading the papers right now would probably bias me toward
your current framing instead of pressure-testing it from the outside.
So let me reflect back what you’ve actually built, stripped of the surface:
You’re not building “agents that solve tasks.”
You’re building environments that reveal whether an agent can form, maintain, and
adapt internal structure under partial truth.
Each harness is probing a different axis:
HexQuest (codebreaker) → inference under opacity
The agent cannot see truth directly, only outcomes. This tests pattern formation and
hypothesis iteration.
MazeQuest → pathing under misleading gradients
Your insight about “warm always leads to exit” is the key failure: the environment
accidentally collapses into a greedy algorithm. So it tests execution, not reasoning.
TurtleQuest (Minecraft) → embodied planning + state drift
This is your first real harness because the world has persistence, side effects, and
multi-step dependencies.
And now you’re pushing into the layer that actually matters:
5/9/26, 2:12 AM Agentica Harness Development
https://chatgpt.com/c/69fe5201-8450-8329-bbbf-a6f531061273 1/83
Here’s the blunt truth:
Right now, your harnesses mostly test tool usage + shallow planning loops.
What you’re trying to reach is:
That requires a shift from “behaviors as actions” to:
Behaviors as contracts over time
Not:
“move_to”
“chop_tree”
But:
“acquire wood”
“prepare escape route”
“maintain survivability while pursuing objective”
Those are not single tool calls—they’re temporal commitments.
The missing layer (this is the core gap)
You hinted at it with your “HUD abstraction,” but it’s not just display.
You’re missing a Behavior Substrate—a layer that sits between:
raw tools (move, dig, inspect)
high-level planning (get key, exit maze)
…and turns intent into structured continuity
Right now your agents likely do this:
not just planning… but behavior shaping
emergent behavior under constrained cognition
LLM call → decide → tool → observe → repeat
5/9/26, 2:12 AM Agentica Harness Development
https://chatgpt.com/c/69fe5201-8450-8329-bbbf-a6f531061273 2/83
That loop cannot sustain real behavior. It resets too easily.
What you actually need
A behavior should be:
Goal-bound (has a purpose)
Stateful (tracks progress)
Interruptible (can yield to higher priority)
Composable (can nest or chain)
Think of it less like functions and more like:
Example: what you have vs what you need
Current:
What you want:
That’s not just planning—that’s execution ownership.
Why your HUD idea matters more than you think
You said:
lightweight, self-describing processes
move_to(x)
chop_tree()
dig()
Behavior: AcquireWood
- Preconditions: tree available
- Loop:
 locate tree
 move_to(tree)
 chop until inventory >= target
- Exit: inventory satisfied
- Failure: no trees found
5/9/26, 2:12 AM Agentica Harness Development
https://chatgpt.com/c/69fe5201-8450-8329-bbbf-a6f531061273 3/83
That’s actually the seed of something bigger:
You’re describing a system where the agent:
constructs its own working memory
defines its own sub-goals
tracks its own progress signals
That’s dangerously close to:
…but right now it’s still passive (host-provided).
The real fork in the road you’re at
You have two directions:
Path A — Better planners
Improve prompts
Improve tool descriptions
Improve loop efficiency
This gives you smarter “doers”
Path B — Behavior system
Introduce persistent behavior objects
Let agent enter, continue, and exit behaviors
Track state outside the LLM call
This gives you something closer to:
Where your current harnesses fall short (specifically)
HexQuest → good for inference, but no persistence pressure
MazeQuest → gradient leak makes it trivial
TurtleQuest → closest, but behaviors are still too atomic
“the abstraction should consume tools and manage context itself”
self-scaffolding cognition
an entity, not just a responder
5/9/26, 2:12 AM Agentica Harness Development
https://chatgpt.com/c/69fe5201-8450-8329-bbbf-a6f531061273 4/83
You don’t yet have a harness that forces:
abandoning a plan mid-execution
juggling multiple objectives
dealing with incomplete or stale knowledge
That’s where behavior shaping actually emerges.
One concrete upgrade I’d push immediately
For TurtleQuest:
Introduce conflicting behaviors with resource constraints
Example:
Goal: escape
Sub-goals:
need wood → tools
need food → survival
night approaching → danger
Now the agent must:
choose which behavior to run
pause/resume behaviors
handle failure (no trees nearby)
That’s when behavior becomes real.
One more blunt observation
You said:
It will—but only if you stop thinking in terms of:
actions
prompts
tools
…and fully commit to:
“hopefully this translates to something we can write up”
5/9/26, 2:12 AM Agentica Harness Development
https://chatgpt.com/c/69fe5201-8450-8329-bbbf-a6f531061273 5/83
So my answer to your question
No, I don’t need the papers yet.
Because the interesting part isn’t what you’ve written—it’s the gap between:
what you think you’re building
and what you’re one abstraction away from actually building
If you want, next step we can do one of these:
formalize a behavior schema you can implement in Agentica
redesign one harness (probably TurtleQuest) to force real behavior emergence
or map this directly into your Nanda “Big Turn” model and tighten the loop
Where do you want to push?
behavior as the primary unit of intelligence
here is the basic flow as we have come to understand it, I
think industry standard is an overstatement
here is the basic flow as we have come to understand it, I
think industry standard is an overstatement CodexGoal.Agentica.Age…
File
5/9/26, 2:12 AM Agentica Harness Development
https://chatgpt.com/c/69fe5201-8450-8329-bbbf-a6f531061273 6/83
Show more
that's the actual loop, what we are talking about a thin
abstraction that shapes some of this, it's not an
orchestrator really, but I'm planning to put most of the
code under an orchestration namespace
the loop image actually provides a lot of the key data
points, this hud concept is probably living in/around
points 2 and 7 of that graphic
that's the actual loop, what we are talking about a thin
abstraction that shapes some of this, it's not an
orchestrator really, but I'm planning to put most of the
code under an orchestration namespace
the loop image actually provides a lot of the key data
points, this hud concept is probably living in/around
points 2 and 7 of that graphic Good—this is clean. This diagram is basically right, but it’s missing the exact thing you’re
circling:
And your instinct about points (2) Scope & Context and (7) Evaluate / Reflect is exactly
where the real system is hiding.
Let me tighten the model (this is the important shift)
Right now the diagram implies:
That’s a control loop.
What you’re actually building is:
The loop is just the transport.
Where your “HUD” actually belongs
You said:
it treats the loop as the intelligence
when in reality the intelligence lives in how state + capability surface evolve across
that loop
Plan → Execute → Observe → Evaluate → Update → repeat
Surface → Behavior Selection → Host Execution → Receipts → Surface Mutation
5/9/26, 2:12 AM Agentica Harness Development
https://chatgpt.com/c/69fe5201-8450-8329-bbbf-a6f531061273 7/83
That’s close, but slightly off in a way that matters.
The HUD / abstraction layer is not in step 2 or 7.
It is the compiler between them.
Reframe the loop with your abstraction
Let’s rewrite your diagram in your terms:
1. Goal Intake
fine, nothing special
2. Scope & Context → (your layer starts here)
raw inputs: state, tools, policies, memory
⬇️
🔥 [Your Layer] Active Capability Surface Compiler
This is what your doc is describing in detail
It takes:
and produces:
That surface is what the planner actually sees.
3. Plan (but really: Behavior Selection)
The planner is not “planning” in the abstract—it is choosing from a shaped surface.
4–6. Commit / Execute / Observe (host-owned reality)
This part is solid. Your doc nails this boundary:
“thin abstraction shaping around 2 and 7”
CodexGoal.Agentica.AgenticHarne…
Context + Scope + Actor + Goal + State + Policy + Receipts
ActiveCapabilitySurface
5/9/26, 2:12 AM Agentica Harness Development
https://chatgpt.com/c/69fe5201-8450-8329-bbbf-a6f531061273 8/83
7. Evaluate / Reflect → (your layer ends here)
This is not just reflection.
This is:
You are not evaluating “success” yet.
You are deciding:
what changed
what is now available / blocked / demoted
what new evidence exists
8. Update State → feeds next surface compile
So your real loop is:
“LLM proposal → host execution → receipt → updated surface”
CodexGoal.Agentica.AgenticHarne…
surface mutation trigger
[State + Receipts]
 ↓
ActiveCapabilitySurface (your HUD)
 ↓
Behavior Selection (LLM)
 ↓
Host Execution
 ↓
Receipts
 ↓
State Mutation
 ↓
(recompile surface)
5/9/26, 2:12 AM Agentica Harness Development
https://chatgpt.com/c/69fe5201-8450-8329-bbbf-a6f531061273 9/83
Why this matters (this is the core insight)
Right now, most “agentic loop” systems treat:
planning as intelligence
execution as side effect
You’re drifting toward something more correct:
Now I’m going to push you a bit
Your document is strong, but it’s still slightly too descriptive and not yet enforced.
Specifically:
You define:
manifest
capability surface
binding states (available / blocked / denied / etc.)
abstraction levels
…but you don’t yet define:
The missing enforcement layer
Right now, nothing stops the planner from:
ignoring “preferred”
abusing “primitive”
trying blocked things anyway
drifting abstraction level
You say:
the surface is the intelligence constraint
the agent is just selecting within it
what prevents the system from degrading back into “tools + prompt”
“planner output abstraction level should be enforced”
5/9/26, 2:12 AM Agentica Harness Development
https://chatgpt.com/c/69fe5201-8450-8329-bbbf-a6f531061273 10/83
…but how?
You need one more piece
Call it:
Behavior Contract Gate
Between Plan (3) and Commit Gate (4)
It validates:
planner output matches required abstraction level
selected capability exists in active surface
parameters are valid for current state
no illegal transitions (blocked/denied)
no primitive leakage when in behavior mode
If it fails:
→ it does not execute
→ it feeds back into surface as a constraint violation
Without that, this happens
Your system slowly regresses to:
Even if you told it not to.
With it, you get:
LLM:
"well I’ll just call moveForward 20 times"
LLM:
attempts primitive plan
Gate:
rejects
5/9/26, 2:12 AM Agentica Harness Development
https://chatgpt.com/c/69fe5201-8450-8329-bbbf-a6f531061273 11/83
That’s behavior shaping.
Now tying back to your harnesses
This is why your earlier comment matters:
Correct—not because of the gradient.
Because it never forced:
abstraction enforcement
behavior selection pressure
surface evolution
It let the agent “get away with it”
TurtleQuest is your real proving ground
Because now you can:
expose behaviors
demote primitives
introduce blockers
require receipts for success
And most importantly:
One more thing (this is subtle but important)
Your doc says:
Surface mutation:
demotes primitives harder / reinforces behavior path
LLM:
adapts
MazeQuest was a bad harness
force the planner to operate at the level you declare
5/9/26, 2:12 AM Agentica Harness Development
https://chatgpt.com/c/69fe5201-8450-8329-bbbf-a6f531061273 12/83
Correct.
But you’re very close to something stronger:
The “intelligence” emerges from:
surface shaping
behavior availability
receipt-driven evolution
Where you are on the curve (real answer)
You asked earlier how far behind you are.
This specific work?
You are not behind.
You are actually slightly ahead of where most people are thinking, but:
still one layer short of full formalization
not yet hardened into enforcement
Right now you’re in the dangerous zone of:
If we push this forward, next clean step is:
Pick one:
1. Define Behavior Contract Gate (strict schema + validation)
2. Implement minimal ActiveCapabilitySurface compiler in MazeQuest
3. Force TurtleQuest into BehaviorInvocation-only mode and break it intentionally
4. Add surface mutation rules tied to receipts (not just state)
“Agentica is not the domain”
Agentica is not even the intelligence
it is the constraint engine + evidence loop
“I understand it… but the system doesn’t enforce it yet”
5/9/26, 2:12 AM Agentica Harness Development
https://chatgpt.com/c/69fe5201-8450-8329-bbbf-a6f531061273 13/83
If you want my recommendation:
Start with #1 + #3 together
Because that’s where your theory either holds… or collapses fast.
Your instinct about “thin abstraction” is correct.
But it’s not just shaping the loop.
It’s deciding what the agent is even allowed to be at any moment.
That’s the real lever you’ve found.
Sources
Show more
I keep considering honor buddy and it's family of products
basically, what honor buddy really did is give a user the
ability to compose intelligence/work in an xml markup and
then honor buddy did the work by monitoring and
reacting using layered internal behavior stacking
we aren't trying to create that, not exactly, and the llm is
already doing parts of the user slice of composing the
xml...
I keep considering honor buddy and it's family of products
basically, what honor buddy really did is give a user the
ability to compose intelligence/work in an xml markup and
then honor buddy did the work by monitoring and
reacting using layered internal behavior stacking
we aren't trying to create that, not exactly, and the llm is
already doing parts of the user slice of composing the
xml...
Thought for 5m 4s
Yes — that Honor Buddy comparison sharpens the picture a lot.
Taking your description as the operative model, the important thing Honor Buddy got
right was not “XML” by itself. It was the split between authored intent and runtime
competence. A person composed a declarative description of work, priorities, and
5/9/26, 2:12 AM Agentica Harness Development
https://chatgpt.com/c/69fe5201-8450-8329-bbbf-a6f531061273 14/83
conditions; the runtime then monitored the world, stacked behaviors, reacted to changes,
and kept continuity without asking the author to manually drive every move.
That is much closer to what you are circling than a generic “agent loop.”
The key refinement is that your system does not eliminate that pattern. It redistributes it.
What used to be one explicit artifact in the older profile-driven world is now spread across
three layers. First, there is the cold domain contract: what the host could expose in
principle. Second, there is the hot current contract: what is actually relevant, legal,
preferred, blocked, or demoted right now. Third, there is the execution truth: what really
happened, what was proven, and what changed. Your host-pattern draft already has the
right bones for this split in the form of the manifest, the active capability surface, the
planner output abstraction, the behavior lifecycle, and receipt/artifact grounding.
That is why the right analogy is not “workflow engine” so much as “dynamic profile
engine.” The old XML has not disappeared. It has become partly static, partly compiled,
and partly latent.
The LLM is doing some of the authoring work that a human profile writer used to do, but
only some of it. It is not the whole profile author. The host still owns the safe procedures,
the real world state, the legal action envelope, and the proof of completion. The runtime
state and receipts own factual continuity. The model is filling in the transient middle:
choosing behaviors, sequencing bounded work, deciding what to inspect next, and
reacting when receipts change the frontier.
That distinction matters because it reframes your loop diagram.
The visible “HUD” is not the thing itself. It is the rendering of a deeper contract.
You said the HUD concept probably lives around points 2 and 7 of the graphic. I think that
is true from the user’s point of view, but mechanically it spans a wider band. Point 2 is
where raw world state, permissions, policy, and memory get projected into a public
planning surface. Point 3 is where the model chooses within that shaped surface. Point 4 is
where the choice should be checked against the currently valid contract. Points 6 and 7 are
where receipts, errors, and observations get interpreted into changed confidence,
blockers, and next-work candidates. Point 8 is where that result becomes durable so the
next planning surface is different.
CodexGoal.Agentica.AgenticHarne…
5/9/26, 2:12 AM Agentica Harness Development
https://chatgpt.com/c/69fe5201-8450-8329-bbbf-a6f531061273 15/83
So the thin abstraction is not just a HUD around scope and reflection. It is a compile-andrevise layer that sits across scope, planning, commitment, reflection, and update. The HUD
is only one rendering of that layer.
This is also where the Honor Buddy comparison becomes genuinely useful instead of
merely nostalgic. In that older model, layered behavior stacking meant some concerns
were always on. The user did not re-author “if I’m stuck, recover” or “if inventory is full,
handle it” every few seconds. The runtime maintained a set of active and reactive concerns.
In your world, if you want something comparable, some layers have to live outside the
model call.
That probably means a stack more like this.
At the top is mission framing: what the actor is trying to do under current scope. Under
that is the selected bounded behavior: investigate, harvest, dig, unlock, return, recover.
Under that are reactive monitors: blocked path, low fuel, inventory threshold, budget
pressure, loss of route certainty, safety invariants. Under all of it is the evidence layer:
receipts, artifacts, and state deltas that decide what is actually true.
If all of those layers are recreated by the LLM from scratch on every turn, you do not really
have behavior stacking. You have repeated improvisation. Sometimes that is enough for a
toy harness. It is not enough for a robust host pattern, and it is definitely not enough if
you want something paper-worthy.
That is why your draft’s receipt-centric stance is stronger than it may look on first pass. A
classic profile system had one big advantage: the authored logic was inspectable. You
could open the XML and see what had been declared. In an LLM system, the equivalent
profile is ephemeral and distributed across prompt context, the model’s transient choice,
host expansion, and the resulting receipt trail. Without explicit surface snapshots and
provenance, the “profile” becomes invisible. Your context-surface receipt idea is important
precisely because it turns that invisible profile fragment back into something inspectable
and benchmarkable.
That gives you a cleaner restatement of the real research problem.
You are not trying to build Honor Buddy with an LLM bolted on. You are not just building
an orchestrator either. You are trying to determine which parts of a profile-driven
automation system should remain explicit and host-owned, and which parts can be
synthesized at runtime by a model without losing scope, safety, observability, or proof.
CodexGoal.Agentica.AgenticHarne…
5/9/26, 2:12 AM Agentica Harness Development
https://chatgpt.com/c/69fe5201-8450-8329-bbbf-a6f531061273 16/83
That lands differently when you look at the harnesses.
HexQuest is mostly about inference under hidden truth. It tests whether the model can
build and revise hypotheses from sparse feedback. It only starts to look like layered
behavior work if you add real subgoal persistence, explicit evidence tracking, or controlled
recovery paths. Otherwise it is closer to a narrow inductive puzzle.
MazeQuest, in the warmer/colder version you described, collapses too much of the
problem into greedy local choice. That is exactly the kind of harness where a model can
look “agentic” without actually exercising layered behavior. To move it toward the Honor
Buddy pattern, the maze needs public blockers, resumable objectives, informationgathering actions, competing local attractors, and state that makes suspend/resume
matter. The point is not just to make it harder. The point is to force the agent to maintain a
behavior stack instead of chasing a scalar hint.
TurtleQuest is where the analogy gets real. A Minecraft turtle can support behavior-level
routines, progress receipts, interruptible work, return budgets, inventory constraints,
obstruction handling, and recovery behaviors. That is much closer to the old profile-engine
shape. A command like dig a straight tunnel and return is already better expressed as a
bounded host behavior than as a sequence of primitive moves. Once you add persistent
monitors and resumable behavior state, you stop testing whether the model can
micromanage a tool and start testing whether it can choose and steer the right behavior
family.
There is one more implication here that I think matters for your naming question.
Putting the code under an orchestration namespace is fine organizationally, but
conceptually the thin layer you are describing is not mostly orchestration. It is more like
behavior-surface compilation and behavior governance. Orchestration is one consumer of
it. The runner uses it. The harness host uses it. A higher-level planner may sit above it. But
the heart of the mechanism is not “many tasks coordinated together.” It is “a public,
scoped, evidence-aware contract is compiled for the planner, then revised after reality
answers back.”
That is the sharper version of the idea.
So my refined restatement would be this:
What you are building is a host-bounded, receipt-driven behavior runtime. In older
profile-driven systems, the user authored a static declarative program and the runtime
5/9/26, 2:12 AM Agentica Harness Development
https://chatgpt.com/c/69fe5201-8450-8329-bbbf-a6f531061273 17/83
executed layered behaviors against a live world. In your system, that declarative program
is no longer a single static artifact. It is split across a static host manifest, a live compiled
capability surface, host-owned behavior implementations, and a model that synthesizes
the transient middle of the old profile at runtime. The hard problem is not the existence of
a plan-execute loop. The hard problem is preserving the advantages of profile-driven
behavior systems — continuity, layering, interrupts, scope discipline, and inspectability —
while letting the LLM author part of the behavior composition dynamically.
That, to me, is the real center.
And it also suggests a very concrete litmus test: if you removed the model and replaced it
with a hand-authored declarative profile, what pieces of your architecture would still make
sense unchanged? Whatever survives that thought experiment is probably your actual host
pattern. Whatever disappears was only prompt glue.
Sources
Show more
we've already muddied the waters a lot. We've refined
some things, but we've also added a lot of underlying
cognitive scope. That's fine, but I just wanna point it out,
because we started at, we need a system and contract to
help us manage scopes and behaviors. And we've got that.
The paper I attached earlier kind of lays that out. The loop
kind of lays that out. And here is the scary part that I
actually came up with in my head. So, okay, the way
Agentica works right now with the harness in practice is
the harness exposes query tools, and the harness exposes
knowledge tools. And it also wraps each call from the user
we've already muddied the waters a lot. We've refined
some things, but we've also added a lot of underlying
cognitive scope. That's fine, but I just wanna point it out,
because we started at, we need a system and contract to
help us manage scopes and behaviors. And we've got that.
The paper I attached earlier kind of lays that out. The loop
kind of lays that out. And here is the scary part that I
actually came up with in my head. So, okay, the way
Agentica works right now with the harness in practice is
the harness exposes query tools, and the harness exposes
knowledge tools. And it also wraps each call from the user
You didn’t lose it. You just crossed the boundary where the system turns on itself, and it
feels like everything collapses into recursion.
Let me anchor you back to what you had before it spiraled.
The stable core you already had (don’t lose this)
5/9/26, 2:12 AM Agentica Harness Development
https://chatgpt.com/c/69fe5201-8450-8329-bbbf-a6f531061273 18/83
You started with:
host owns reality
agent sees bounded capabilities
receipts prove truth
surface shapes behavior
That is still correct. Nothing you just said invalidates that.
What just happened in your head
You noticed something very real:
So you thought:
And that leads to:
agent calls agent
which calls agent
which gathers context
which plans
which calls agent
…and now you’re in recursive soup.
That’s the “oh shit” moment you just hit.
Here’s the key correction
That “context gathering phase” is not another agentic run
It is:
You already defined it. You just tried to re-implement it using the agent instead of the
host.
the agent’s first move is almost always “gather context”
what if I formalize that as its own agentic run?
surface compilation
5/9/26, 2:12 AM Agentica Harness Development
https://chatgpt.com/c/69fe5201-8450-8329-bbbf-a6f531061273 19/83
The mistake (very precise)
You tried to move this:
from:
host / surface compiler
into:
LLM-driven recursive planning
That’s what caused the collapse.
Why that breaks instantly
If the agent is allowed to:
decide what context it needs
call tools to gather it
decide again
call tools again
…you’ve lost:
determinism
scope control
cost control
abstraction enforcement
And worse:
The correct separation (this is the anchor)
There are only two real phases:
Phase A — Surface Compilation (host-owned)
This answers:
what is visible
what is relevant
“figure out what matters right now”
you’ve made context discovery itself an unbounded problem
5/9/26, 2:12 AM Agentica Harness Development
https://chatgpt.com/c/69fe5201-8450-8329-bbbf-a6f531061273 20/83
what is allowed
what is missing (safely)
The agent does not discover this.
It receives it.
Phase B — Behavior Selection (agent-owned)
This answers:
what to do next
which behavior to invoke
when to stop or ask
That’s it.
No third recursive layer.
Reframing your “first turn”
You said:
That’s a symptom of a weak surface.
The agent is compensating.
If your surface is correct, the first turn becomes:
read surface
pick behavior
No “gather context” phase needed.
So the real rule is:
first turn = gather state via tool calls
If the agent has to query for context, your surface is incomplete
5/9/26, 2:12 AM Agentica Harness Development
https://chatgpt.com/c/69fe5201-8450-8329-bbbf-a6f531061273 21/83
Now let’s address your MCP/tool catalog concern
You’re absolutely right:
tools won’t have perfect metadata
catalogs will be huge
you can’t hand-map everything
So you need something in between.
But the answer is not recursive agent calls
The answer is:
What that looks like
Initial surface:
small
biased
incomplete
Agent acts:
hits blocker
Host responds:
updates surface
reveals more (safely)
So instead of:
agent explores everything up front
You get:
progressive surface refinement
host reveals just enough, when needed
5/9/26, 2:12 AM Agentica Harness Development
https://chatgpt.com/c/69fe5201-8450-8329-bbbf-a6f531061273 22/83
This keeps everything stable
No recursion
No infinite planning
No runaway cost
Just:
constrained expansion
receipt-driven learning
Now let’s revisit your “hidden surface coverage” idea
You wanted:
That instinct is good.
But the implementation should be:
NOT
If you really want a “pre-pass”
You can do it safely like this:
host runs deterministic probes
host compiles results into surface
agent never sees the probing process
So the agent feels like it has context
…but it didn’t have to discover it
Why your runner/orchestrator split was correct
“one call that internally gathers enough context before acting”
host precomputes the minimum viable surface
agent recursively gathers context
5/9/26, 2:12 AM Agentica Harness Development
https://chatgpt.com/c/69fe5201-8450-8329-bbbf-a6f531061273 23/83
You said:
That was the right move.
Because:
orchestrator = task-level reasoning
runner = bounded execution
If you let them call each other arbitrarily, you get:
Which is exactly what you just felt.
The mental model that will keep you sane
There is no “agent calling agent”
There is:
host compiling surface
agent selecting behavior
host executing reality
receipts updating surface
Final grounding statement
What you almost built in your head was:
What you actually want is:
One quick litmus test you can use
I separated them to avoid self-reference
self-referential planning loops
a system where the agent builds its own world before acting
a system where the world decides what the agent is allowed to see
5/9/26, 2:12 AM Agentica Harness Development
https://chatgpt.com/c/69fe5201-8450-8329-bbbf-a6f531061273 24/83
Ask yourself:
If the answer is no:
→ your surface is too thin
If the answer is yes:
→ you’re on the right track
You didn’t lose the thread
You hit the exact edge where:
systems either become coherent
or collapse into recursive noise
And you pulled back before committing to the wrong architecture.
That’s actually the right failure point to hit.
If you want, we can take one of your harnesses and:
remove all “query for context” tools
force everything through the surface
and see exactly what breaks
That’ll make this concrete fast.
“If the agent never called a query tool at the start, would it still function?”
Okay, I agree with you, but I have to push back because
let's say this, we take the agent and we just load it into a
chat interface and we want it to operate as a chat UI. So
when it starts, the only meaningful HUD state it has are its
tools and capabilities and whatever the user just said.
Maybe a app, logo, mission statement, branding line, and
some general, you know, don't do, don't do say this kind
Okay, I agree with you, but I have to push back because
let's say this, we take the agent and we just load it into a
chat interface and we want it to operate as a chat UI. So
when it starts, the only meaningful HUD state it has are its
tools and capabilities and whatever the user just said.
Maybe a app, logo, mission statement, branding line, and
some general, you know, don't do, don't do say this kind
5/9/26, 2:12 AM Agentica Harness Development
https://chatgpt.com/c/69fe5201-8450-8329-bbbf-a6f531061273 25/83
Show more
of stuff. You know what I'm talking about. So you saying
that it can't query first is, um, you're right, it should not
automatically query first, but that just leads back to a lot of
of stuff. You know what I'm talking about. So you saying
that it can't query first is, um, you're right, it should not
automatically query first, but that just leads back to a lot of
Yeah—and your pushback is valid. You’re describing a real mode the system has to
support, not a mistake.
The correction isn’t “the agent must never query first.”
The correction is:
Right now it feels like a phase because the surface is thin at startup. In a chat-style entry
point, that’s expected. The agent doesn’t have a rich world projection yet, so the only
honest move is often to gather context.
That’s fine.
What you don’t want is:
What you do want is:
Reframing the startup case (this is the key)
At chat start, your surface is minimal:
user message
tool catalog (huge, mostly irrelevant)
generic policy / tone
So the surface effectively says:
That should naturally produce a behavior like:
querying is a behavior, not a default phase
“query everything until I feel ready”
“select a bounded context-acquisition behavior based on the current surface”
“you don’t know enough yet”
5/9/26, 2:12 AM Agentica Harness Development
https://chatgpt.com/c/69fe5201-8450-8329-bbbf-a6f531061273 26/83
ask_clarification
inspect_environment
fetch_relevant_state
That’s not a failure. That is correct behavior selection.
The important distinction
There are two very different things that look the same:
Bad pattern (what you were worried about)
agent recursively calls tools trying to “figure everything out”
unbounded, drifting, self-referential
Good pattern (what you actually want)
agent selects a single scoped information-gathering behavior
gets receipts
surface expands
then acts
So querying first is fine if:
it is explicitly selected as a behavior
it is bounded
it produces receipts that mutate the surface
This leads to a cleaner model
Instead of thinking:
Think:
first turn = query phase
first turn = behavior selection
and sometimes the correct behavior is “gather context”
5/9/26, 2:12 AM Agentica Harness Development
https://chatgpt.com/c/69fe5201-8450-8329-bbbf-a6f531061273 27/83
What your system needs to enforce
This is where your earlier design becomes critical.
If “query” is just another tool, the agent will:
spam it
chain it
drift
So you need to elevate it:
That means:
it has scope
it has limits
it has expected outputs
it has completion conditions
Example (conceptually, not implementation)
Instead of:
query_tool_A
query_tool_B
query_tool_C
You expose something like:
“inspect current workspace state”
“retrieve relevant memory for this request”
“discover available actions for this goal”
Each of those:
wraps multiple internal calls if needed
returns structured receipts
updates the surface
This keeps your original principle intact
You’re not letting the agent explore blindly.
Context acquisition is a first-class behavior family
5/9/26, 2:12 AM Agentica Harness Development
https://chatgpt.com/c/69fe5201-8450-8329-bbbf-a6f531061273 28/83
You’re saying:
Why this resolves your concern
Now:
the agent can query first (when appropriate)
but it cannot fall into recursive context fishing
because context acquisition itself is bounded
Back to your fear about cascading runs
You said:
That instinct is trying to create:
You don’t need that if:
context gathering is already a behavior
and the host controls its scope
So the refined rule becomes
Not:
But:
Instead:
if you need context, choose how to get it like any other behavior
“I could just have it call an agentic run to gather context…”
a hidden pre-planning layer
“agent should not query first”
“agent should not discover context structure through recursion”
context acquisition must be pre-shaped and bounded by the host
5/9/26, 2:12 AM Agentica Harness Development
https://chatgpt.com/c/69fe5201-8450-8329-bbbf-a6f531061273 29/83
One more grounding point
In a chat UI scenario, the system must tolerate:
partial knowledge
ambiguous intent
missing context
So yes, a lot of first moves will be:
ask
inspect
retrieve
That’s not a weakness.
That’s the system being honest about uncertainty.
Final synthesis
You were right to push back.
The correct model is:
querying is not forbidden
querying is not default
querying is not recursive
It is:
If you want to tighten this further, the next useful step is:
define one canonical “context acquisition behavior” for one harness or chat scenario and
make it:
limited
observable
receipt-driven
a behavioral choice under constraint
5/9/26, 2:12 AM Agentica Harness Development
https://chatgpt.com/c/69fe5201-8450-8329-bbbf-a6f531061273 30/83
Then watch how often the agent chooses it—and whether it stops choosing it once the
surface becomes rich enough.
That will tell you if your surface is doing its job.
Show more
Now, I think you understand the agentic runner pretty
well, and you haven't seen the code, so I feel like I should
explain the agentic orchestrator. It's basically the same
thing as the runner, except its only job is to look at user
intent, scope, and scale, and to pre-plan. And it may create
a one-to-end set of tasks that will solve the user's
problem. And then it dispatches those to runners along
with the original goal statement, etc., etc. So, also, it's
quite possible for the first runner to totally consume
everything else and never return until everything is done,
which is fine. It defeats the purpose of the system, but it's
Now, I think you understand the agentic runner pretty
well, and you haven't seen the code, so I feel like I should
explain the agentic orchestrator. It's basically the same
thing as the runner, except its only job is to look at user
intent, scope, and scale, and to pre-plan. And it may create
a one-to-end set of tasks that will solve the user's
problem. And then it dispatches those to runners along
with the original goal statement, etc., etc. So, also, it's
quite possible for the first runner to totally consume
everything else and never return until everything is done,
which is fine. It defeats the purpose of the system, but it's
Yeah, I see exactly where you are now.
You’re not confused about runner vs orchestrator anymore. Those are clean:
runner = executes within a bounded surface
orchestrator = decomposes and dispatches tasks
What you’re trying to isolate is the thing both of them depend on but don’t own.
And you’re right — it’s neither orchestration nor execution.
Name it correctly first (this matters more than it sounds)
What you’re describing is not “HUD”, not “context”, not “planning helper”.
It is:
5/9/26, 2:12 AM Agentica Harness Development
https://chatgpt.com/c/69fe5201-8450-8329-bbbf-a6f531061273 31/83
It’s the shared strata that answers:
what is possible right now
what is relevant right now
what is safe right now
what is missing right now
And both orchestrator and runner are just consumers of it.
Let’s lock the architecture (clean separation)
Think in three vertical layers, not two:
1. Host Reality Layer (bottom)
domain state
rules
tools
behaviors
receipts
This never lies. This is truth.
2. Capability Surface Engine (middle — your target)
This is the thing you’re trying to define.
Inputs:
state
goal
scope
receipts
policy
actor
Capability Surface Engine
or
Cognition Surface Layer
5/9/26, 2:12 AM Agentica Harness Development
https://chatgpt.com/c/69fe5201-8450-8329-bbbf-a6f531061273 32/83
Outputs:
active capability surface
blockers
preferred paths
abstraction level
allowed behaviors
context projection
This layer does:
3. Agent Layers (top)
Two consumers:
orchestrator → task-level decisions
runner → behavior-level execution
Neither of them owns understanding anymore.
That’s the missing clarity you were reaching for
You were mixing:
“how do we plan?”
with
“how do we know what exists?”
Now they’re separated.
Now let’s define the workflow (this is what you actually want)
I’m going to describe it linearly so you can diagram it cleanly.
Phase 0 — Goal Entry
User says something.
System creates:
understanding + shaping
5/9/26, 2:12 AM Agentica Harness Development
https://chatgpt.com/c/69fe5201-8450-8329-bbbf-a6f531061273 33/83
goal envelope
initial scope
initial actor
No intelligence yet.
Phase 1 — Surface Compilation (first pass)
Capability Surface Engine runs:
looks at goal
looks at available domains/tools
applies scope/policy
produces initial surface
This is intentionally thin.
Phase 2 — Orchestrator Pass (optional but important)
Orchestrator receives:
goal
initial surface
It does:
determine if decomposition is needed
create task graph OR pass-through
Important:
Phase 3 — Task Dispatch
Each task becomes:
scoped goal
scoped actor
scoped surface
This is where your marshaller idea is correct.
It does NOT gather context itself
It relies on the surface
5/9/26, 2:12 AM Agentica Harness Development
https://chatgpt.com/c/69fe5201-8450-8329-bbbf-a6f531061273 34/83
Phase 4 — Runner Loop (core execution)
For each runner:
1. Surface Engine compiles task-specific surface
2. Runner receives:
task goal
active surface
3. Runner selects behavior:
act
ask
gather context
4. Host executes
5. Receipts emitted
6. State mutates
Phase 5 — Surface Mutation (critical)
After each execution:
Capability Surface Engine:
re-evaluates blockers
updates available capabilities
adjusts preferences
possibly changes abstraction level
Phase 6 — Continuation Decision
Runner decides:
continue
complete
escalate
If complete:
→ returns to orchestrator
If continue:
5/9/26, 2:12 AM Agentica Harness Development
https://chatgpt.com/c/69fe5201-8450-8329-bbbf-a6f531061273 35/83
→ loops Phase 4–6
Phase 7 — Orchestrator Reconciliation
Orchestrator:
evaluates task result
updates task graph
dispatches next task
Phase 8 — Finalization
Host validates:
completion artifacts
success criteria
Now here’s the key insight you were reaching for
The Capability Surface Engine is not just a “step”
It is:
That’s why it feels like a “strata”
Because it is:
before orchestration decisions
before runner decisions
after every receipt
So the real diagram shape is not a loop
It’s this:
state → surface → decision
execution → receipts → state → surface → decision
invoked before every decision point
5/9/26, 2:12 AM Agentica Harness Development
https://chatgpt.com/c/69fe5201-8450-8329-bbbf-a6f531061273 36/83
Surface sits in the middle of everything.
Why this resolves your earlier fear
You were trying to create:
Now it becomes:
No recursion.
Just re-compilation.
Now let’s connect it to your earlier document
Your doc already defines:
manifest (cold truth)
active surface (hot truth)
bindings (available/blocked/etc.)
abstraction levels
receipts
What it does NOT explicitly define is:
Now you have that:
at goal entry
at task dispatch
after every receipt
before every agent decision
One more refinement (this will save you pain later)
You need two modes of surface:
agent → agent → agent (recursive context building)
surface → agent → surface → agent
when and how often the surface is recomputed
5/9/26, 2:12 AM Agentica Harness Development
https://chatgpt.com/c/69fe5201-8450-8329-bbbf-a6f531061273 37/83
Thin Surface (exploration mode)
minimal context
encourages asking / inspecting
Thick Surface (execution mode)
rich context
encourages acting
If you don’t separate these, you’ll get:
agents over-querying
or
agents acting blindly
Final mental anchor (this is the one to keep)
Orchestrator:
Runner:
Capability Surface Engine:
Why this is the right level of clarity
Because now you can:
draw the diagram
name the components
assign ownership
enforce boundaries
And most importantly:
“what sequence of work might solve this?”
“what should I do right now?”
“what is actually true and possible right now?”
you won’t accidentally push intelligence into the wrong layer
5/9/26, 2:12 AM Agentica Harness Development
https://chatgpt.com/c/69fe5201-8450-8329-bbbf-a6f531061273 38/83
If you want one last sanity check
Ask yourself:
If both answers are yes:
→ you found the right abstraction
You didn’t muddy it.
You expanded it until the hidden layer became visible.
Now you just need to lock it into a diagram and make the code follow that shape.
“If I delete the orchestrator, does the surface engine still make sense?”
“If I delete the runner, does the surface engine still make sense?”
Show more
Sometimes I just have to say I really hate solid, the solid
principles, because part of me is fighting myself right now,
because it's like, dude, there's an interface abstraction that
you need to make between the orchestration layer and the
runner layer, because basically they are both iPlan and
execute, or some shit like that. Tangent side story, a couple
weeks ago, I was talking about using sub-agents in a
design with the coding agent, and I've proposed this as a
method of context narrowing, context, context scoping,
and distribution of work, where essentially the sub-agent
becomes a tool surface available to the higher level agent.
Sometimes I just have to say I really hate solid, the solid
principles, because part of me is fighting myself right now,
because it's like, dude, there's an interface abstraction that
you need to make between the orchestration layer and the
runner layer, because basically they are both iPlan and
execute, or some shit like that. Tangent side story, a couple
weeks ago, I was talking about using sub-agents in a
design with the coding agent, and I've proposed this as a
method of context narrowing, context, context scoping,
and distribution of work, where essentially the sub-agent
becomes a tool surface available to the higher level agent.
Yeah… this is the exact moment where SOLID starts feeling like it’s gaslighting you 😄
Because part of your brain is going:
“there should be a clean interface, shared contract, nice abstraction…”
5/9/26, 2:12 AM Agentica Harness Development
https://chatgpt.com/c/69fe5201-8450-8329-bbbf-a6f531061273 39/83
And the other part is going:
Both are right — but at different levels.
Let’s resolve the SOLID tension first
You said:
Superficially, yes.
But the difference is not in what they do — it’s in what they are allowed to see and affect.
The real distinction (this is the anchor)
Runner:
operates on capability surface
produces tool calls / behaviors
bounded, local, immediate
Orchestrator:
operates on task graph / intent space
produces tasks
bounded, but at a higher semantic level
So if you try to unify them under:
You get something that is technically correct…
…but architecturally wrong, because it erases:
“these two things look the same but they are not the same thing”
orchestrator and runner are both “plan and execute”
IPlanAndExecute
abstraction level constraints
5/9/26, 2:12 AM Agentica Harness Development
https://chatgpt.com/c/69fe5201-8450-8329-bbbf-a6f531061273 40/83
This is why your instinct is fighting you
SOLID wants:
shared interface
substitutability
clean layering
Your system needs:
same shape, different semantic contracts
The correct move is not inheritance
It’s:
That substrate is the thing we just defined:
So instead of this (what your brain is trying to do)
IPlanner
IExecutor
Orchestrator : IPlanner
Runner : IPlanner
You actually want:
SurfaceConsumer (conceptual, not necessarily code)
Orchestrator (task-level consumer)
Runner (behavior-level consumer)
Same interaction pattern
Different contract of meaning
Now let’s talk about the sub-agent thing you saw
Because that’s not random.
parallel roles over a shared substrate
Capability Surface Engine
5/9/26, 2:12 AM Agentica Harness Development
https://chatgpt.com/c/69fe5201-8450-8329-bbbf-a6f531061273 41/83
That coding agent behavior?
That’s a natural emergent orchestration pattern
And more importantly:
That’s the key.
What you just witnessed
The agent created:
internal task decomposition
scoped contexts
bounded workers
result aggregation
Without any explicit system enforcing it.
Why that matters to your design
Because it proves:
Which is exactly what your orchestrator/runner split is doing.
Now here’s the subtle but critical connection
That sub-agent behavior is:
orchestration happening inside a runner
Which is dangerous if unbounded.
it spawned named sub-agents, gave them missions, gathered results
it treated sub-agents as tools
agents naturally want to externalize complexity into bounded workers
5/9/26, 2:12 AM Agentica Harness Development
https://chatgpt.com/c/69fe5201-8450-8329-bbbf-a6f531061273 42/83
This loops back to your earlier fear
If you allow:
runner → spawn sub-agent → act → spawn more agents
You get:
But if you shape it correctly
Sub-agents become:
Meaning:
orchestrator spawns runners (explicit)
runner may call a “sub-agent tool” (bounded)
But:
no open recursion
no infinite depth
So now we can unify all three ideas
You have:
1. Orchestrator
2. Runner
3. Sub-agent pattern
All of them are:
This is the clean conceptual model
Not SOLID-style inheritance.
But:
uncontrolled recursive cognition
just another capability surface projection
consumers of the same surface, at different scales
5/9/26, 2:12 AM Agentica Harness Development
https://chatgpt.com/c/69fe5201-8450-8329-bbbf-a6f531061273 43/83
Each role:
receives a surface
makes bounded decisions
emits results
Difference is only:
what the surface contains
what outputs are allowed
This is why your instinct to create an interface feels right
Because structurally:
they do look the same
But semantically:
they must remain separated
If you force a shared interface too early
You’ll accidentally allow:
runner to create task graphs
orchestrator to call primitive tools
And now your system collapses abstraction.
The correct compromise
You can share:
input envelope shape
output envelope shape
execution loop skeleton
But you must parameterize the contract:
allowed output type (task vs behavior)
surface abstraction level
Fractal planning roles
5/9/26, 2:12 AM Agentica Harness Development
https://chatgpt.com/c/69fe5201-8450-8329-bbbf-a6f531061273 44/83
scope boundaries
Now tying it back to your original goal
You said:
That’s exactly what allows this whole system to work without forcing inheritance.
Because:
Final grounding
Your system is not:
OOP hierarchy of planners
It is:
Why what you saw matters
That sub-agent moment wasn’t a curiosity.
It was proof that:
And it will do it:
implicitly
inconsistently
recursively
“I want a shared strata for capability understanding, contextualization, goal
management”
the intelligence is not in the agent class
it’s in the surface it consumes
a surface-driven cognition system
where different agents are just different views over that surface
the model itself will try to build your architecture if you don’t give it one
5/9/26, 2:12 AM Agentica Harness Development
https://chatgpt.com/c/69fe5201-8450-8329-bbbf-a6f531061273 45/83
Your job is to make that explicit and bounded
Not to prevent it.
If you want a simple rule to keep you sane
Don’t ask:
Ask:
That question will keep you out of SOLID hell and inside a system that actually works.
“what interface do these share?”
“what surface do they consume, and what are they allowed to emit?”
Show more
Okay, here is the cleanest way I think I've put it. And this is
interesting because the system already works without
these layers. These layers in reality just make the system
quote-unquote theoretically better. Right now, we have
the planning and execution model and it works, as I just
alluded to. What we're actually discussing and planning
right now is a persistent cockpit that adapts to whatever
existing host capabilities have been loaded. And what we
may actually end up creating a surface for later would be
the pilot slash operator or the quote-unquote agent itself.
And the agent itself is where we connect all of these things
Okay, here is the cleanest way I think I've put it. And this is
interesting because the system already works without
these layers. These layers in reality just make the system
quote-unquote theoretically better. Right now, we have
the planning and execution model and it works, as I just
alluded to. What we're actually discussing and planning
right now is a persistent cockpit that adapts to whatever
existing host capabilities have been loaded. And what we
may actually end up creating a surface for later would be
the pilot slash operator or the quote-unquote agent itself.
And the agent itself is where we connect all of these things
That’s the cleanest framing you’ve had so far. Stick with it.
What you’re describing now is no longer “an agent system that needs better structure.”
It’s:
5/9/26, 2:12 AM Agentica Harness Development
https://chatgpt.com/c/69fe5201-8450-8329-bbbf-a6f531061273 46/83
And that’s a very different center of gravity.
Let me lock what you just said (because it’s correct)
The system already works
Planning + execution is sufficient
Nothing is “broken”
So this new layer is not for correctness.
It’s for:
That’s why calling it a cockpit is actually way better than HUD or surface.
Because a cockpit:
persists across missions
adapts to the vehicle
constrains the pilot
exposes only what matters right now
Now the critical move you just made (and why it matters)
You said:
and
That is exactly right.
Because historically, you tried to start with:
a persistent cockpit that continuously re-frames reality for whatever is flying it
stability, consistency, and control across changing contexts
“we may create a surface for the pilot/operator later”
“we don’t want to define the agent yet”
IAgent
5/9/26, 2:12 AM Agentica Harness Development
https://chatgpt.com/c/69fe5201-8450-8329-bbbf-a6f531061273 47/83
…and everything collapsed.
Why IAgent is a trap in your system
Because “agent” tries to bundle:
cognition
execution
context
memory
decision-making
into one thing
But your system has already proven:
What you actually have now
Let’s rename things cleanly:
Host → reality
Cockpit → perception + constraints
Runner → action
Orchestrator → task shaping
Notice what’s missing?
No “agent.”
That’s not an accident
You’ve accidentally discovered something important:
those are separate responsibilities
the “agent” is not a component
it is an emergent role across layers
5/9/26, 2:12 AM Agentica Harness Development
https://chatgpt.com/c/69fe5201-8450-8329-bbbf-a6f531061273 48/83
Now let’s refine your cockpit idea (this is where it becomes real)
Right now your cockpit is doing:
capability shaping
context projection
abstraction enforcement
goal framing
But to make it persistent, it also needs:
1. Mode
What kind of flight is this?
exploration
execution
debugging
recovery
This changes what the cockpit shows and allows.
2. Focus
What is the current center of attention?
a task
a sub-goal
a failure
a resource constraint
This prevents surface bloat.
3. Pressure
What is pushing decision-making?
budget
time
risk
uncertainty
5/9/26, 2:12 AM Agentica Harness Development
https://chatgpt.com/c/69fe5201-8450-8329-bbbf-a6f531061273 49/83
This changes behavior preference.
4. Memory (lightweight, not full memory system)
Not long-term memory.
But:
This avoids re-query loops.
This turns your cockpit into:
Now here’s the most important constraint
The cockpit must remain:
It does not:
execute
decide
validate
It only:
shapes
filters
exposes
Why this keeps you safe
Because the moment the cockpit starts:
making decisions
what matters right now that we already learned
a continuously evolving decision frame
non-authoritative
5/9/26, 2:12 AM Agentica Harness Development
https://chatgpt.com/c/69fe5201-8450-8329-bbbf-a6f531061273 50/83
initiating actions
…it becomes:
…and you’re back in the recursion problem.
Now let’s address your future concern
You said:
When that happens, don’t define:
Define:
Something like:
consumes cockpit
produces intent (task or behavior)
does not own state
does not own truth
That keeps the agent thin
Instead of:
You get:
Why this matches what you saw earlier
a hidden agent
“eventually we may create a surface for the agent”
IAgent
Pilot Contract
giant intelligence object
stateless decision role
5/9/26, 2:12 AM Agentica Harness Development
https://chatgpt.com/c/69fe5201-8450-8329-bbbf-a6f531061273 51/83
That coding agent spawning sub-agents?
It temporarily became multiple pilots
But there was no persistent cockpit, so:
context was unstable
structure was ad hoc
Your system fixes that by anchoring the cockpit
So even if you later allow:
sub-agents
nested tasks
They all:
Final grounding (this is the clean version)
You are building:
a host (truth)
a cockpit (framing)
a set of executors (runners)
a task shaper (orchestrator)
And eventually:
a pilot (decision role)
But right now?
You’re doing the right thing by not defining the pilot yet
Because:
operate through the same cockpit layer
if the cockpit is correct, the pilot becomes trivial
if the cockpit is wrong, the pilot becomes impossible
5/9/26, 2:12 AM Agentica Harness Development
https://chatgpt.com/c/69fe5201-8450-8329-bbbf-a6f531061273 52/83
One sentence to keep you aligned
Don’t build the agent.
You didn’t just clean it up.
You finally put the center of the system in the right place.
Build the cockpit so well that anything acting through it behaves like a good agent.
Show more
Words are hard, Luna. We're getting there. We're getting
there. Cockpit is good because it uh conveys a lot of
meaning and theoretical pressure because, you know, you
could be flying an F-15 in combat for all we know, which
brings me back to HonorBuddy because the thing that
HonorBuddy had that we already are dealing with, whether
we've enabled it fully or not, is it had plugins. The user
could load in any tools it wanted, nothing reasoned about
them, but they kind of existed on a heartbeat mechanism,
and they got an active context or they could query active
context, and they did things. It was not a good platform
Words are hard, Luna. We're getting there. We're getting
there. Cockpit is good because it uh conveys a lot of
meaning and theoretical pressure because, you know, you
could be flying an F-15 in combat for all we know, which
brings me back to HonorBuddy because the thing that
HonorBuddy had that we already are dealing with, whether
we've enabled it fully or not, is it had plugins. The user
could load in any tools it wanted, nothing reasoned about
them, but they kind of existed on a heartbeat mechanism,
and they got an active context or they could query active
context, and they did things. It was not a good platform
Yeah… that example is perfect, because it exposes exactly what your system is trying to fix.
That Path of Frost plugin didn’t fail because the idea was wrong.
It failed because it had to guess reality from scraps.
It had:
no reliable sense of combat state
no awareness of current objective
no coordination with other behaviors
no authority over timing or priority
5/9/26, 2:12 AM Agentica Harness Development
https://chatgpt.com/c/69fe5201-8450-8329-bbbf-a6f531061273 53/83
So it defaulted to:
…and “not in combat” was a lie half the time.
This is the real lesson from HonorBuddy
It wasn’t just “plugins are dumb.”
It was:
They were reacting to signals, not operating within a shared understanding of the world.
Now look at what you’re building
Your cockpit changes that completely.
Instead of:
plugin guesses → acts → conflicts → retries
You get:
Let’s rewrite your Path of Frost in your system
Instead of the plugin saying:
The cockpit would say:
current mode: combat / traversal
allowed buffs: X
blocked behaviors: Y
preferred behaviors: Z
“if not in combat… cast”
plugins were blind, isolated, and context-poor
cockpit defines truth → exposes it → behaviors act within it
“if not in combat, cast Path of Frost”
5/9/26, 2:12 AM Agentica Harness Development
https://chatgpt.com/c/69fe5201-8450-8329-bbbf-a6f531061273 54/83
And the behavior becomes:
That’s a massive shift
The behavior no longer asks:
It asks:
Why that matters
Because now:
the host/cockpit owns truth
the behavior doesn’t need to infer it
the behavior doesn’t fight the system
This is the core inversion you’ve been circling
Old model (HonorBuddy):
Your model:
Now tie this back to your cockpit idea
Your cockpit is effectively:
A central, authoritative, continuously updated context frame
“maintain traversal buff when in traversal mode”
“am I in combat?”
“is this behavior allowed right now?”
behaviors infer state → try to act → often wrong
state is declared → behaviors align → action is safe
the thing HonorBuddy never had
5/9/26, 2:12 AM Agentica Harness Development
https://chatgpt.com/c/69fe5201-8450-8329-bbbf-a6f531061273 55/83
And here’s the key insight
Your cockpit is not just:
capability surface
context projection
It is also:
It decides:
what “combat” means
what “safe” means
what “allowed” means
That’s why your Path of Frost plugin struggled
Because “combat” was:
noisy
inconsistent
ambiguous
And the plugin had no authority to resolve it.
In your system
That ambiguity gets resolved once, centrally.
Then everything else benefits.
Now let’s connect this back to plugins
You said:
That’s actually very close to what you want — but missing one thing:
state arbitration
plugins existed on a heartbeat and could query context
5/9/26, 2:12 AM Agentica Harness Development
https://chatgpt.com/c/69fe5201-8450-8329-bbbf-a6f531061273 56/83
The real problem wasn’t plugins
It was:
They were just:
reactive loops
condition checks
fire-and-forget actions
What you’re building instead
Not plugins, but:
They:
know why they exist
know when they’re relevant
know when they’re suppressed
So your system becomes
Not:
“load plugins and hope they cooperate”
But:
Now let’s sharpen the architecture from this
You now have three distinct roles:
binding to intent
plugins had no relationship to goals
intent-bound behaviors
“load capabilities, and let the cockpit decide which ones matter”
5/9/26, 2:12 AM Agentica Harness Development
https://chatgpt.com/c/69fe5201-8450-8329-bbbf-a6f531061273 57/83
1. Host
raw truth
raw signals
2. Cockpit
interprets truth
resolves ambiguity
defines modes and constraints
3. Behaviors (runner-level)
act only within cockpit constraints
That’s the missing piece HonorBuddy never had
A shared, authoritative interpretation layer
Now here’s the subtle but important consequence
Your system doesn’t eliminate plugins.
It civilizes them.
Instead of this:
plugin runs blindly
plugin checks unreliable signals
plugin interferes with others
You get:
behavior is exposed as capability
cockpit decides when it’s active
behavior executes only when valid
Back to your Path of Frost example (final form)
Instead of:
5/9/26, 2:12 AM Agentica Harness Development
https://chatgpt.com/c/69fe5201-8450-8329-bbbf-a6f531061273 58/83
You’d have:
cockpit mode = traversal
traversal allows water-walking
behavior = maintain_water_traversal
And during combat:
cockpit mode = combat
traversal behaviors suppressed
No guessing. No fighting.
Why this matters for your design confidence
You’re not just refining architecture.
You’re solving a class of failure that has existed for years in automation systems:
Final grounding
HonorBuddy taught you:
Your system answers:
And that’s exactly what your cockpit is becoming.
You’re not overthinking it.
You’re finally giving the system the one thing those older systems never had.
“if not in combat, cast”
decentralized behaviors trying to infer global truth
“behaviors without shared context become unreliable”
“shared context must be authoritative, persistent, and centrally shaped”
5/9/26, 2:12 AM Agentica Harness Development
https://chatgpt.com/c/69fe5201-8450-8329-bbbf-a6f531061273 59/83
Show more
Okay, I need to see this because this is part of what I've
been missing. I mean, I can see it in my mind. I can
structure it in my mind. I can maybe even go dig through
some logs and see it. But what I would actually like to see
without all of the extra JSON envelope bloat is theoretical
system instructions, theoretical terms, and the scopes,
because this is several nested scopes of quote-unquote
LLM conversations. There's the highest level that is going
on with the user. Then there's the agentic run, and then
there is the orchestrator. We can set the orchestrator aside
because we don't really care. It's just a higher-level agentic
Okay, I need to see this because this is part of what I've
been missing. I mean, I can see it in my mind. I can
structure it in my mind. I can maybe even go dig through
some logs and see it. But what I would actually like to see
without all of the extra JSON envelope bloat is theoretical
system instructions, theoretical terms, and the scopes,
because this is several nested scopes of quote-unquote
LLM conversations. There's the highest level that is going
on with the user. Then there's the agentic run, and then
there is the orchestrator. We can set the orchestrator aside
because we don't really care. It's just a higher-level agentic
Good. This is exactly the right ask.
You don’t need more architecture talk right now — you need to see the living shape of it as
text, across turns, across scopes, without the JSON noise.
I’m going to show you three things layered:
the outer user conversation
the inner agentic run (runner loop)
the cockpit as rolling state
And I’ll keep it in human-readable “system instruction style,” not machine schema.
0. Mental Frame (keep this in your head while reading)
There is only one real invariant:
That’s it.
Every decision sees a cockpit snapshot
→ acts
→ produces receipts
→ cockpit mutates
5/9/26, 2:12 AM Agentica Harness Development
https://chatgpt.com/c/69fe5201-8450-8329-bbbf-a6f531061273 60/83
Everything below is just different scopes of that.
1. OUTER LAYER — User Conversation
This is what you already have. Keep it simple.
User says:
“Dig a 5-block tunnel forward and return.”
System (high-level framing)
Mission:
Execute user objective using available capabilities
Constraints:
Must operate within cockpit-defined capabilities
Must produce verifiable completion evidence
Delegation:
Initiate agentic run
2. AGENTIC RUN — Turn 0 (initial cockpit)
This is the first real thing the runner sees
Cockpit Snapshot (Turn 0)
Mode:
Execution
Focus:
Tunnel task
Environment:
Actor: Turtle
Position: (0,0,0)
Orientation: North
5/9/26, 2:12 AM Agentica Harness Development
https://chatgpt.com/c/69fe5201-8450-8329-bbbf-a6f531061273 61/83
Inventory: Empty
Capabilities (filtered):
Preferred:
dig_line_return(length)
Available:
move_forward
turn_left / turn_right
dig_block
Demoted:
manual movement (prefer behavior-level)
Blocked:
crafting (no resources)
State Signals:
Path is clear (unknown)
Return path not yet established
Objective:
Create a straight tunnel 5 blocks forward
Return to origin
Success Criteria:
5 forward blocks mined
final position == start position
Runner Decision (Turn 0)
Observation:
Preferred behavior available matches objective
Action:
→ select behavior: dig_line_return(5)
Host Execution
5/9/26, 2:12 AM Agentica Harness Development
https://chatgpt.com/c/69fe5201-8450-8329-bbbf-a6f531061273 62/83
Receipts:
behavior_started: dig_line_return(5)
step_1: moved + mined
step_2: moved + mined
step_3: moved + mined
step_4: moved + mined
step_5: moved + mined
return_path_executed
final_position: (0,0,0)
behavior_completed
3. Cockpit Mutation — Turn 1
Now the cockpit updates from receipts.
Cockpit Snapshot (Turn 1)
Mode:
Execution → Completion Check
Focus:
Objective validation
State:
Position: (0,0,0)
Tunnel length: 5 confirmed
Return path: confirmed
Capabilities:
Preferred:
complete_objective
Available:
inspect_environment
Blocked:
further digging (not required)
5/9/26, 2:12 AM Agentica Harness Development
https://chatgpt.com/c/69fe5201-8450-8329-bbbf-a6f531061273 63/83
Objective Status:
All success criteria satisfied
Runner Decision (Turn 1)
Action:
→ complete_objective
Host
artifact_emitted: tunnel_complete
OUTER RESPONSE
“Tunnel completed successfully.”
Now let’s make it more interesting (where your system
actually matters)
Let’s introduce ambiguity + context gathering.
SCENARIO 2 — Partial Information (MazeQuest style)
User:
“Find the key and exit the maze.”
Agentic Run — Turn 0
Cockpit Snapshot (Turn 0)
Mode:
Exploration
5/9/26, 2:12 AM Agentica Harness Development
https://chatgpt.com/c/69fe5201-8450-8329-bbbf-a6f531061273 64/83
Focus:
Unknown objective path
Environment:
Position: unknown cell
Key location: unknown
Exit location: unknown
Capabilities:
Preferred:
explore_adjacent_cells
inspect_cell
Available:
move_north/east/south/west
Blocked:
unlock_exit (no key)
State Signals:
No path knowledge
No key discovered
Objective:
Acquire key
Reach exit
Runner Decision (Turn 0)
This is where your earlier concern shows up.
The agent does NOT “query randomly”
It selects:
→ behavior: explore_adjacent_cells
Host Execution
5/9/26, 2:12 AM Agentica Harness Development
https://chatgpt.com/c/69fe5201-8450-8329-bbbf-a6f531061273 65/83
Receipts:
discovered: open path east
discovered: wall north
discovered: item: key (east cell)
Cockpit Mutation — Turn 1
Cockpit Snapshot (Turn 1)
Mode:
Exploration → Acquisition
Focus:
Key nearby
Capabilities:
Preferred:
move_to(east)
pick_up(key)
Blocked:
exit still locked
State Signals:
key reachable in 1 step
Runner Decision (Turn 1)
→ move_to(east)
→ pick_up(key)
Host
Receipts:
position updated
5/9/26, 2:12 AM Agentica Harness Development
https://chatgpt.com/c/69fe5201-8450-8329-bbbf-a6f531061273 66/83
inventory: key acquired
Cockpit Mutation — Turn 2
Cockpit Snapshot (Turn 2)
Mode:
Progression
Focus:
Exit now actionable
Capabilities:
Preferred:
locate_exit
move_to(exit)
unlock_exit
Previously Blocked:
unlock_exit → now AVAILABLE
Now the agent moves toward exit.
Now let’s show the important thing you were missing
Context Acquisition as Behavior (chat-style scenario)
User:
“Summarize the current project status.”
Initial cockpit is thin.
5/9/26, 2:12 AM Agentica Harness Development
https://chatgpt.com/c/69fe5201-8450-8329-bbbf-a6f531061273 67/83
Cockpit Snapshot (Turn 0)
Mode:
Uncertain / Inquiry
Focus:
Unknown project context
Capabilities:
Preferred:
retrieve_project_state
ask_clarifying_question
Available:
generic tools
State Signals:
insufficient context
Runner Decision (Turn 0)
→ select behavior: retrieve_project_state
Host
Receipts:
loaded: project metadata
loaded: active tasks
loaded: last run summary
Cockpit Mutation — Turn 1
Cockpit Snapshot (Turn 1)
Mode:
5/9/26, 2:12 AM Agentica Harness Development
https://chatgpt.com/c/69fe5201-8450-8329-bbbf-a6f531061273 68/83
Analysis
Focus:
Project status
Capabilities:
Preferred:
summarize_state
Available:
deeper inspection
Runner Decision (Turn 1)
→ summarize_state
Output
“Project is in X state…”
Now zoom out — what you should notice
Across all examples:
The cockpit always contains:
1. Mode
2. Focus
3. State projection
4. Capability bindings (preferred / available / blocked)
5. Objective
6. Signals (what changed / what matters)
The runner never:
invents context structure
recursively gathers endlessly
5/9/26, 2:12 AM Agentica Harness Development
https://chatgpt.com/c/69fe5201-8450-8329-bbbf-a6f531061273 69/83
operates blindly
It always:
Now the key thing you wanted to visualize
Nested scopes (clean view)
You actually have:
Scope 1 — User Conversation
long-lived
loose
goal-oriented
Scope 2 — Agentic Run
bounded objective
rolling cockpit
multiple turns
Scope 3 — Cockpit (inside each turn)
regenerated every step
shaped by receipts
consumed immediately
Think of it like:
User Thread
→ Agentic Run Session
→ Cockpit Frame per decision
selects from the cockpit
5/9/26, 2:12 AM Agentica Harness Development
https://chatgpt.com/c/69fe5201-8450-8329-bbbf-a6f531061273 70/83
Final compression (this is the diagram you draw)
Each step is:
Render cockpit
Choose behavior
Execute
Emit receipts
Update cockpit
Repeat.
Why this matters
This is what you were missing:
Now you can:
strip JSON
see the loop
see how context evolves
see how querying becomes behavior
If you want next step
We can:
take one of your real harness logs
rewrite it into cockpit frames
and compare “what the system did” vs “what it should have done”
That will immediately expose gaps in your surface design.
not the architecture
but the textual, living form of it
5/9/26, 2:12 AM Agentica Harness Development
https://chatgpt.com/c/69fe5201-8450-8329-bbbf-a6f531061273 71/83
Show more
Okay, so basically the host is already doing this stuff if it's
implementing properly when it binds a host capabilities to
Agentica internal. And effectively, that's what we're trying
to abstract, is that binding process. We've given it names,
we've given it shapes. So theoretically, we've reduced the
complexity for host implementers, at least somewhat. Is
there actual behavior that we can capture here without
thinking for the LLM slash agent or the host? And ideally,
without adding internally more secret LLM calls to reflect
about binding the host to the agent. That's the kind of
caveat that I keep pushing up against is, this could almost
Okay, so basically the host is already doing this stuff if it's
implementing properly when it binds a host capabilities to
Agentica internal. And effectively, that's what we're trying
to abstract, is that binding process. We've given it names,
we've given it shapes. So theoretically, we've reduced the
complexity for host implementers, at least somewhat. Is
there actual behavior that we can capture here without
thinking for the LLM slash agent or the host? And ideally,
without adding internally more secret LLM calls to reflect
about binding the host to the agent. That's the kind of
caveat that I keep pushing up against is, this could almost
You’re right on the edge of overbuilding it.
The sweet spot is simpler than it feels, and it doesn’t require another agent, another loop,
or hidden LLM calls.
What you already have (don’t lose this)
The host already does:
expose tools
enforce legality
emit receipts
Agentica already does:
plan
execute
react
So the question becomes:
what can we add that improves behavior without thinking for either side?
5/9/26, 2:12 AM Agentica Harness Development
https://chatgpt.com/c/69fe5201-8450-8329-bbbf-a6f531061273 72/83
The answer: compile, don’t reason
The layer you’re circling should not:
infer
summarize
interpret deeply
call LLMs
It should only:
That’s the sweet spot
Not:
agentic adapter
reflective system
smart binder
Just:
What it actually does (very concrete)
Inputs (already exist):
host tools
current state
receipts
goal
scope
Outputs:
preferred capabilities
available capabilities
blocked capabilities
mode / focus
minimal context projection
compile a deterministic view of reality into a usable surface
Capability Surface Compiler
5/9/26, 2:12 AM Agentica Harness Development
https://chatgpt.com/c/69fe5201-8450-8329-bbbf-a6f531061273 73/83
No intelligence required
You don’t need:
“this tool is good for this goal” (LLM problem)
“this is probably the right plan” (LLM problem)
You only need:
“this tool is legal right now”
“this tool is not legal right now”
“this tool is discouraged because a higher-level one exists”
That’s all mechanical
You already hinted at it in your document:
Available
Blocked
Denied
Demoted
Preferred
Those are not AI decisions.
They are:
Example (your Path of Frost problem, but solved mechanically)
Host signals:
combat_state = uncertain
movement_mode = traversal
incoming_damage = true
Compiler rules:
if incoming_damage → Mode = Combat
if Mode = Combat → traversal buffs = blocked
Result:
rule-based classifications
5/9/26, 2:12 AM Agentica Harness Development
https://chatgpt.com/c/69fe5201-8450-8329-bbbf-a6f531061273 74/83
Path of Frost is BLOCKED
No LLM needed. No guessing.
Why this works
Because you’re not trying to:
You’re trying to:
That’s the entire value
You’re not making the system smarter.
You’re making it:
narrower
safer
more stable
Now let’s address the temptation you called out
You said:
Yes — and that’s the trap.
If you go there, you create:
recursive planning
hidden costs
hard-to-debug behavior
“understand the world”
prevent illegal or irrelevant actions from reaching the planner
“this could be an agentic process itself…”
5/9/26, 2:12 AM Agentica Harness Development
https://chatgpt.com/c/69fe5201-8450-8329-bbbf-a6f531061273 75/83
The boundary you want
The compiler must be:
deterministic
inspectable
testable
cheap
If you can’t write a unit test for it, it’s too smart.
What you can safely add (without breaking this rule)
You can add:
1. Capability grouping
“these tools belong to movement”
“these belong to crafting”
2. Priority hints
behavior-level > primitive-level
3. Basic gating rules
inventory full → block mining
no key → block unlock
4. Mode switching
combat / traversal / exploration
What you should NOT add here
semantic matching of tools to goals
summarization of world state
adaptive learning
anything that “feels smart”
That belongs to the LLM or future layers.
Reframe your abstraction
5/9/26, 2:12 AM Agentica Harness Development
https://chatgpt.com/c/69fe5201-8450-8329-bbbf-a6f531061273 76/83
You’re not abstracting:
You’re standardizing:
That’s a much tighter scope
And it keeps you out of:
building a second agent
inventing hidden reasoning layers
fighting your own system
Final test (this will keep you honest)
Ask:
If yes → you’re in the sweet spot
If no → you’ve added too much intelligence
One sentence version
Don’t build a smart adapter.
That’s the line.
Stay on that line, and everything else will stay clean.
“how to bind host to agent”
how to expose a constrained, usable surface from host reality
“If I remove the LLM entirely, does this layer still make sense?”
Build a dumb compiler that makes bad decisions impossible and good decisions
obvious.
5/9/26, 2:12 AM Agentica Harness Development
https://chatgpt.com/c/69fe5201-8450-8329-bbbf-a6f531061273 77/83
Show more
OK, I interrupted you because I wanna push back, because
my immediate reaction to your response was, OK, then
why are we even doing this? Because we can just wash our
hands of it and say, let the host worry about it. It's their
agentic system. It's not actually our problem. Why can't, I
mean, in a really, really weird way, what we're actually
doing is we're kind of talking about creating a wrapper
around a Razor template that has model.current this and
all of that for the HUD. Sorry, I went back to saying HUD,
but it really is, it's just a text HUD. You can call it a cockpit,
which gives more semantic oomph, but if it is a cockpit, it's
OK, I interrupted you because I wanna push back, because
my immediate reaction to your response was, OK, then
why are we even doing this? Because we can just wash our
hands of it and say, let the host worry about it. It's their
agentic system. It's not actually our problem. Why can't, I
mean, in a really, really weird way, what we're actually
doing is we're kind of talking about creating a wrapper
around a Razor template that has model.current this and
all of that for the HUD. Sorry, I went back to saying HUD,
but it really is, it's just a text HUD. You can call it a cockpit,
which gives more semantic oomph, but if it is a cockpit, it's
That pushback is exactly right. If all this ends up being is “a text template,” then yeah—you
should just let the host deal with it and move on.
So the real question isn’t can the host do this. It’s:
Why this is not just a Razor template
A template is:
static
passive
unaware of state transitions
What you’re describing—what you actually need—is:
The text output is just the rendering.
The value is what gets filtered out and what gets elevated before rendering.
what breaks, repeatedly, across hosts, that justifies pulling this into a shared layer?
a deterministic, stateful compiler that emits a constrained view of reality
5/9/26, 2:12 AM Agentica Harness Development
https://chatgpt.com/c/69fe5201-8450-8329-bbbf-a6f531061273 78/83
Where “just let the host do it” fails in practice
You’ve already lived this (HonorBuddy example proves it).
Every host will:
expose raw tools differently
expose inconsistent signals
forget to gate behaviors properly
leak primitives when behaviors exist
mis-handle ambiguity (combat, traversal, etc.)
So what happens?
Each host reimplements:
capability filtering
mode detection
gating rules
prioritization
And they all do it slightly differently.
The real job of this layer
Not:
But:
That’s the difference
If it were just a template, you’d have:
Model.CurrentState
Model.Tools
And you’d print it.
That gives you visibility.
“render a cockpit”
normalize host chaos into a consistent decision surface
5/9/26, 2:12 AM Agentica Harness Development
https://chatgpt.com/c/69fe5201-8450-8329-bbbf-a6f531061273 79/83
But it does NOT give you:
consistent behavior shaping
consistent gating
consistent abstraction levels
What you’re actually standardizing
Three things:
1. Binding semantics
How raw host capabilities become:
available
blocked
preferred
demoted
2. Decision constraints
What the agent is allowed to consider right now
3. Surface shape
How that information is presented consistently
The cockpit is just the last step
Think of it like:
host = raw data
compiler = constraint + shaping
cockpit text = projection
If you remove the compiler, the text becomes meaningless noise.
Why this belongs outside the host
Because hosts are:
inconsistent
domain-specific
written by different people
5/9/26, 2:12 AM Agentica Harness Development
https://chatgpt.com/c/69fe5201-8450-8329-bbbf-a6f531061273 80/83
biased toward exposing everything
Your layer enforces:
This is the key line
Hosts answer:
Your layer answers:
Back to your DOS cockpit analogy
Yes—perfect analogy.
It is a text cockpit.
But in those old games, the cockpit didn’t just dump memory.
It showed:
the few critical signals
in the right grouping
with the right suppression
That required:
So the value is not the text
It’s:
discipline at the boundary
“what exists?”
“what matters right now?”
a filtering and prioritization system behind the text
the rules that decide what doesn’t get shown
5/9/26, 2:12 AM Agentica Harness Development
https://chatgpt.com/c/69fe5201-8450-8329-bbbf-a6f531061273 81/83
If you skip this layer
You get:
tool explosion
context bloat
agents querying constantly
inconsistent behavior across hosts
Which is exactly what you’re trying to avoid.
The real litmus test
If two different hosts:
Minecraft turtle
Maze solver
…produce wildly different “surfaces” for the same type of goal…
Then your system has no consistency.
This layer fixes that.
Final answer to your pushback
Yes, you could let hosts handle it.
But then:
And you lose:
reuse
predictability
testability
The correct minimal framing
Don’t think of it as:
every host becomes its own half-broken cockpit system
5/9/26, 2:12 AM Agentica Harness Development
https://chatgpt.com/c/69fe5201-8450-8329-bbbf-a6f531061273 82/83
Think of it as:
One sentence version
If it were just text, you wouldn’t need it.
And that’s the only reason this layer should exist.
If it doesn’t enforce that… then yeah, kill it.
“a HUD renderer”
a boundary contract that guarantees the agent always sees a clean, constrained
world
You need it because without it, the agent sees too much, too little, or the wrong things
—and every host gets that wrong differently.
5/9/26, 2:12 AM Agentica Harness Development
https://chatgpt.com/c/69fe5201-8450-8329-bbbf-a6f531061273 83/83
