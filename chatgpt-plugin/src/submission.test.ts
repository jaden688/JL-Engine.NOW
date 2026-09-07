import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { describe, expect, it } from "vitest";
import { toolAnnotations } from "./contracts.js";

const submissionPath = fileURLToPath(new URL("../../chatgpt-app-submission.json", import.meta.url));
const submission = JSON.parse(readFileSync(submissionPath, "utf8"));

describe("submission artifact", () => {
  it("matches the live tool names and impact hints", () => {
    expect(Object.keys(submission.tools)).toEqual(Object.keys(toolAnnotations));
    for (const [name, annotations] of Object.entries(toolAnnotations)) {
      expect(submission.tools[name].annotations).toEqual({
        readOnlyHint: annotations.readOnlyHint,
        openWorldHint: annotations.openWorldHint,
        destructiveHint: annotations.destructiveHint,
      });
    }
  });

  it("contains exactly five positive and three negative tests", () => {
    expect(submission.test_cases).toHaveLength(5);
    expect(submission.negative_test_cases).toHaveLength(3);
  });
});
