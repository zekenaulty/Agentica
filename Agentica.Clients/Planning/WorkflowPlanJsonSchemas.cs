namespace Agentica.Clients.Planning;

internal static class WorkflowPlanJsonSchemas
{
    public const string InitialPlan =
        """
        {
          "type": "object",
          "additionalProperties": false,
          "properties": {
            "planId": {
              "type": "string",
              "description": "Unique id for this plan slice."
            },
            "description": {
              "type": "string",
              "description": "Short operator-readable plan description."
            },
            "steps": {
              "type": "array",
              "minItems": 1,
              "items": {
                "type": "object",
                "additionalProperties": false,
                "properties": {
                  "stepId": {
                    "type": "string"
                  },
                  "toolId": {
                    "type": "string"
                  },
                  "kind": {
                    "type": "string",
                    "enum": [
                      "Query",
                      "Action",
                      "PlannerAssist",
                      "Validation",
                      "Synthesis"
                    ]
                  },
                  "effect": {
                    "type": "string",
                    "enum": [
                      "ReadOnly",
                      "WritesLocalState",
                      "ExternalSideEffect",
                      "Destructive",
                      "Unknown"
                    ]
                  },
                  "input": {
                    "type": "object",
                    "additionalProperties": true
                  },
                  "dependsOn": {
                    "type": "array",
                    "items": {
                      "type": "string"
                    }
                  },
                  "batchId": {
                    "type": ["string", "null"]
                  },
                  "reason": {
                    "type": ["string", "null"]
                  },
                  "intent": {
                    "type": "object",
                    "additionalProperties": false,
                    "properties": {
                      "action": {
                        "type": "string"
                      },
                      "rationale": {
                        "type": "string"
                      },
                      "expectedOutcome": {
                        "type": ["string", "null"]
                      }
                    },
                    "required": [
                      "action",
                      "rationale",
                      "expectedOutcome"
                    ]
                  }
                },
                "required": [
                  "stepId",
                  "toolId",
                  "kind",
                  "effect",
                  "input",
                  "dependsOn",
                  "batchId",
                  "reason",
                  "intent"
                ]
              }
            },
            "completionCondition": {
              "type": "string"
            }
          },
          "required": [
            "planId",
            "description",
            "steps",
            "completionCondition"
          ]
        }
        """;

    public const string Refinement =
        """
        {
          "type": "object",
          "additionalProperties": false,
          "properties": {
            "fromPlanId": {
              "type": ["string", "null"]
            },
            "reason": {
              "type": "string",
              "enum": [
                "observation",
                "blocked",
                "ambiguous_action",
                "low_confidence",
                "conflicting_signals",
                "completion_check",
                "continue",
                "resource_risk",
                "retry_unblock"
              ]
            },
            "evidence": {
              "type": "array",
              "minItems": 1,
              "items": {
                "type": "object",
                "additionalProperties": false,
                "properties": {
                  "kind": {
                    "type": "string"
                  },
                  "refId": {
                    "type": "string"
                  }
                },
                "required": [
                  "kind",
                  "refId"
                ]
              }
            },
            "refinedPlan": {
              "type": "object",
              "additionalProperties": false,
              "properties": {
                "planId": {
                  "type": "string",
                  "description": "Unique id for this refined plan slice."
                },
                "description": {
                  "type": "string",
                  "description": "Short operator-readable plan description."
                },
                "steps": {
                  "type": "array",
                  "minItems": 1,
                  "items": {
                    "type": "object",
                    "additionalProperties": false,
                    "properties": {
                      "stepId": {
                        "type": "string"
                      },
                      "toolId": {
                        "type": "string"
                      },
                      "kind": {
                        "type": "string",
                        "enum": [
                          "Query",
                          "Action",
                          "PlannerAssist",
                          "Validation",
                          "Synthesis"
                        ]
                      },
                      "effect": {
                        "type": "string",
                        "enum": [
                          "ReadOnly",
                          "WritesLocalState",
                          "ExternalSideEffect",
                          "Destructive",
                          "Unknown"
                        ]
                      },
                      "input": {
                        "type": "object",
                        "additionalProperties": true
                      },
                      "dependsOn": {
                        "type": "array",
                        "items": {
                          "type": "string"
                        }
                      },
                      "batchId": {
                        "type": ["string", "null"]
                      },
                      "reason": {
                        "type": ["string", "null"]
                      },
                      "intent": {
                        "type": "object",
                        "additionalProperties": false,
                        "properties": {
                          "action": {
                            "type": "string"
                          },
                          "rationale": {
                            "type": "string"
                          },
                          "expectedOutcome": {
                            "type": ["string", "null"]
                          }
                        },
                        "required": [
                          "action",
                          "rationale",
                          "expectedOutcome"
                        ]
                      }
                    },
                    "required": [
                      "stepId",
                      "toolId",
                      "kind",
                      "effect",
                      "input",
                      "dependsOn",
                      "batchId",
                      "reason",
                      "intent"
                    ]
                  }
                },
                "completionCondition": {
                  "type": "string"
                }
              },
              "required": [
                "planId",
                "description",
                "steps",
                "completionCondition"
              ]
            }
          },
          "required": [
            "fromPlanId",
            "reason",
            "evidence",
            "refinedPlan"
          ]
        }
        """;
}
