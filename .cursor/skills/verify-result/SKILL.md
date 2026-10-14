---
name: verify-result
description: "Prove a result before calling it done. Use whenever finishing a task, reviewing a worker's result, or acting as the swarm QA verifier."
---
# Verify Result

Nothing is done without fresh proof. That is Work Doctrine rule 6. A build that succeeded, a window that appeared, or a short slogan does not close the task.

## Run it again

Check the tree you have now. Name the build or the time you ran. A result saved from an older tree does not count.

Do the thing the way a person would. A log or a debugger can explain what you already saw. It does not replace doing the action.

Cover the paths that can change the answer: success, bad input, empty data, cancel, and a reload or restart when the change is supposed to stick. If you skip one, say which and why.

Read the side effect. Open the file, the saved record, the test output, or the call that was supposed to happen. A status line by itself is not enough.

Wait until the real end state is there. A fixed sleep is not a result.

## The note a verifier returns

Put these labels on their own lines, in this order: Claim, How tested, Evidence, Paths covered, Verdict with reason.

Claim is the result you will stand behind. How tested is the command or action you just ran, not a memory of an earlier run. Evidence is the output, exit code, or file text from that run. Paths covered lists success, error, empty, cancel, and persistence, and names anything skipped. Verdict starts with pass or fail and then gives a reason. A pass with no reason is a fail. A fail sends the task back.

The verifier runs the check again. The worker's note is not the proof. A missing heading is a fail, and the note says what is missing. A dialog, a real login, or hardware that a script cannot drive is marked for a person to check. It is not marked pass.

## Leave live systems alone

Risky, costly, or irreversible actions (spending money, publishing, deleting, or touching live systems) stay off unless the user explicitly approves them. Stop and ask.

That limit stays in place unless the agent and the person using this desk both agree, for a stated reason, that it has to come off.
