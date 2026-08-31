Create a new organization on Hugging Face for the AgentBridge project.

Steps:
1. Open the page https://huggingface.co/settings/organizations with your browser.
2. If the page shows a login form, call fill_login_fields. That method handles login for
   you: it fills the fields from saved credentials if any exist, otherwise it asks the
   user (system notification) and WAITS up to 2 minutes for them to log in by hand.
   - If it returns {"filled": [...]}: you are logged in, continue with step 3.
   - If it returns {"login_completed": true}: the user logged in, continue with step 3.
   - If it returns {"no_saved_credentials": true} or {"multiple_credentials": true}: the
     user did not complete the login — STOP and tell the user the login was not completed
     (they may not have valid credentials for this site). Do NOT try to guess credentials.
3. Once logged in and on the organizations settings page, look for the button to create a
   new organization (usually "New organization").
4. Fill the form with:
   - Name: Graphene-Lab
   - Username/slug: graphene-lab
   - Any other fields: sensible defaults.
5. Confirm the creation.
6. Verify the organization exists (the page should show graphene-lab in the list of
   organizations) and take a screenshot.
7. Report exactly what you did and the final result: created or failed, and why.
