export const PROTOCOL_VERSION: 1;
export type JsonValue = null | boolean | number | string | JsonValue[] | { [key: string]: JsonValue };
export type JsonObject = { [key: string]: JsonValue };
export type Awaitable<T> = T | PromiseLike<T>;
/** Format-checked service digest, not a client-verified cryptographic equality. */
export type HostRequestDigest = `sha256:${string}`;

export interface HostKnowledgeFact {
  key: string;
  summary: string;
  value: JsonValue;
  state?: 'observed' | 'inferred' | 'supported' | 'refuted' | 'stale';
  evidenceObservationIds?: string[] | null;
  supersedes?: string | null;
}
export interface HostObservation {
  observationId: string;
  revision: number;
  observedAt: string;
  data: JsonObject;
  facts?: HostKnowledgeFact[] | null;
}
export interface HostCapability {
  id: string;
  name: string;
  description: string;
  kind: 'query' | 'action' | 'plannerAssist' | 'validation' | 'synthesis';
  effect: 'readOnly' | 'writesLocalState' | 'externalSideEffect' | 'destructive';
  inputSchema: {
    fields: Array<{
      name: string;
      type?: 'any' | 'string' | 'integer' | 'number' | 'boolean' | 'object' | 'array';
      required?: boolean;
      description?: string | null;
      allowedValues?: string[] | null;
      example?: JsonValue;
      minimum?: number | null;
      maximum?: number | null;
    }>;
    allowAdditionalProperties?: boolean;
  };
  durableHandoff?: boolean;
}
export interface HostProviderSettings {
  provider?: string;
  model?: string | null;
  thinkingEffort?: string | null;
  maxOutputTokens?: number;
  contextWindowTokens?: number;
  includeThoughtSummaries?: boolean;
  geminiApi?: string | null;
  thinkingEffortResolved?: boolean;
}
export interface HostRunLimits {
  maxSteps?: number;
  maxRefinements?: number;
  maxPlanContinuations?: number;
  timeoutSeconds?: number;
  actionTimeoutSeconds?: number;
  maxRecentObservations?: number;
  maxRecentReceipts?: number;
}
export interface HostRunRequest {
  protocolVersion: 1;
  hostId: string;
  sessionId: string;
  sessionEpoch: string;
  scopeId: string;
  perspectiveId: string;
  objectiveId: string;
  objective: string;
  observation: HostObservation;
  capabilities: HostCapability[];
  provider?: HostProviderSettings | null;
  limits?: HostRunLimits | null;
}
export interface HostRunUsage {
  steps: number;
  refinements: number;
  continuations: number;
  providerCalls: number;
}
export interface HostStartSubmission { attemptId: string; request: HostRunRequest; }
export interface HostStartKey { hostId: string; sessionId: string; attemptId: string; }
export interface HostStartReference extends HostStartKey { runId: string; acceptedRequestHash: HostRequestDigest; }
export interface HostStartTerminal {
  reason: 'stopped' | 'interrupted';
  usage: HostRunUsage | null;
  usageUnknown: boolean;
  settledAt: string;
}
export interface HostStartEntry {
  schema: 1;
  attemptId: string;
  runId: string;
  originalRequest: HostRunRequest;
  acceptedRequest: HostRunRequest;
  originalRequestHash: HostRequestDigest;
  acceptedRequestHash: HostRequestDigest;
  manifestHash: HostRequestDigest;
  stopRequested: boolean;
  /** Null means not settled, including when stopRequested is true. */
  terminal: HostStartTerminal | null;
  admittedAt: string;
}
export interface HostStartSnapshot {
  runId: string;
  hostId: string;
  sessionId: string;
  sessionEpoch: string;
  objectiveId: string;
  scopeId: string;
  perspectiveId: string;
  manifestHash: HostRequestDigest;
  startAdmission: HostStartEntry;
}
export interface HostActionRequest {
  actionId: string;
  runId: string;
  runnerRunId: string;
  stepId: string;
  sessionId: string;
  sessionEpoch: string;
  capabilityId: string;
  manifestHash: string;
  arguments: JsonObject;
  expectedRevision: number;
  deadlineAt: string;
}
export interface HostCompletion { objectiveId: string; evidenceId: string; summary: string; }
export interface HostOperationAdmission { operationId: string; summary: string; usage?: JsonObject | null; }
export interface HostActionResult {
  actionId: string;
  sessionId: string;
  sessionEpoch: string;
  disposition: 'applied' | 'refused' | 'conflict' | 'unavailable' | 'unresolved';
  beforeRevision: number;
  afterRevision: number;
  evidenceId: string;
  summary: string;
  observation?: HostObservation | null;
  completion?: HostCompletion | null;
  operation?: HostOperationAdmission | null;
}
export interface HostActionRecord { fingerprint: string; status: 'pending' | 'unresolved' | 'completed'; result?: HostActionResult; }
export interface HostOperationEvent {
  hostId: string;
  sessionId: string;
  sessionEpoch: string;
  actionId: string;
  operationId: string;
  sequence: number;
  eventId: string;
  kind: 'progress' | 'transfer' | 'completed' | 'blocked' | 'cancelled' | 'decision';
  observation: HostObservation;
  summary: string;
  usage?: JsonObject | null;
  completion?: HostCompletion | null;
  nextSessionEpoch?: string | null;
}
export interface HostDecisionAdoption { adoptionId: string; nextSessionEpoch: string; observation: HostObservation; }
export interface HostOperationEntry {
  request: HostRunRequest;
  action: HostActionRequest;
  admission: HostActionResult | null;
  activeEpoch: string;
  state: 'reserved' | 'parked' | 'waking' | 'continued' | 'completed' | 'blocked' | 'cancelled';
  [field: string]: unknown;
}
export interface HostServiceMessage {
  protocolVersion: 1;
  type: string;
  payload: Record<string, unknown>;
  requestId?: string | null;
  runId?: string | null;
}
export type HostFetch = (url: string, options?: {
  method: string; headers: Record<string, string>; body: string;
}) => PromiseLike<{ ok: boolean; status: number; text(): PromiseLike<string> }>;
export interface HostHttpOptions { fetchImpl?: HostFetch; }
export interface HostSocket {
  readonly readyState: number;
  addEventListener(type: string, listener: (event: { data?: unknown }) => void): void;
  send(data: string): void;
  close(): void;
}
export type HostReconcileAction = (action: HostActionRequest, retained: HostActionRecord | null, entry?: unknown) => Awaitable<HostActionResult | null | undefined>;
export interface AgenticaHostOptions {
  url: string;
  onAction: (action: HostActionRequest) => Awaitable<HostActionResult>;
  loadAction?: (key: string) => Awaitable<HostActionRecord | null | undefined>;
  saveAction?: (key: string, record: HostActionRecord) => Awaitable<void>;
  reconcileAction?: HostReconcileAction;
  onMessage?: (message: HostServiceMessage) => void;
  onStatus?: (status: { state: string; detail?: string }) => void;
  WebSocketImpl?: new (url: string) => HostSocket;
  maxMessages?: number;
  maxActions?: number;
  maxQueuedActions?: number;
  maxMessageBytes?: number;
  requestTimeoutMs?: number;
}
export interface HostRunBinding {
  hostId?: string;
  sessionId: string;
  sessionEpoch: string;
  objectiveId?: string;
  capabilities?: HostCapability[];
  manifestHash?: string;
  startAdmission?: HostStartEntry;
}
export interface HostRecoveryOutcome {
  actionId?: string;
  runId?: string;
  sessionEpoch?: string;
  status: 'resolved' | 'unresolved' | 'live_run' | 'error';
  resolved: boolean;
  duplicate?: boolean;
  code?: string | null;
  message?: string;
}
export function canonicalJson(value: unknown): string;
export function actionKey(action: Pick<HostActionRequest, 'sessionId' | 'sessionEpoch' | 'runId' | 'actionId'>): string;
export function actionFingerprint(action: HostActionRequest): string;
export class AgenticaHost {
  constructor(options: AgenticaHostOptions);
  readonly connected: boolean;
  messages: HostServiceMessage[];
  droppedMessages: number;
  actions: Map<string, HostActionRecord>;
  runs: Map<string, HostRunBinding>;
  operations: Map<string, HostOperationEntry>;
  stoppedRuns: Set<string>;
  actionQueue: Promise<void>;
  socket: HostSocket | null;
  connect(): Promise<void>;
  close(): void;
  request(type: string, payload: unknown, options?: { runId?: string; timeoutMs?: number; binding?: HostRunBinding }): Promise<HostServiceMessage>;
  start(request: Omit<HostRunRequest, 'protocolVersion'> & { protocolVersion?: 1 }): Promise<Record<string, unknown> & { runId: string }>;
  /** One frozen attempt; uncertain delivery never retries automatically. */
  startDurable(submission: HostStartSubmission): Promise<HostStartSnapshot>;
  /** Exact read only. Not found rejects and remains UNKNOWN; no run is registered. */
  getStart(key: HostStartKey, options?: HostHttpOptions): Promise<HostStartEntry>;
  /** Fences local effects before HTTP. terminal:null does not establish settlement. */
  stopStart(reference: HostStartReference, options?: HostHttpOptions): Promise<HostStartEntry>;
  resume(reference: { runId: string; sessionId: string; sessionEpoch: string }): Promise<HostServiceMessage>;
  stop(runId: string): Promise<HostServiceMessage>;
  listOperations(options: { hostId: string; sessionId: string } & HostHttpOptions): Promise<HostOperationEntry[]>;
  signalOperation(event: HostOperationEvent, options?: HostHttpOptions): Promise<unknown>;
  stopDecision(event: HostOperationEvent, options?: HostHttpOptions): Promise<unknown>;
  wakeOperation(event: HostOperationEvent): Promise<Record<string, unknown> & { runId: string }>;
  adoptDecision(event: HostOperationEvent, adoption: HostDecisionAdoption): Promise<Record<string, unknown> & { runId: string }>;
  recover(options: { hostId: string; sessionId: string; reconcile?: HostReconcileAction } & HostHttpOptions): Promise<HostRecoveryOutcome[]>;
}
