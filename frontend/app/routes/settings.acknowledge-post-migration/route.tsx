import type { Route } from "./+types/route";
import { backendClient } from "~/clients/backend-client.server";
import { redirect } from "react-router";
import { isAuthenticated } from "~/auth/authentication.server";

export async function action({ request }: Route.ActionArgs) {
    if (!await isAuthenticated(request)) return redirect("/login");

    await backendClient.acknowledgePostMigration();
    return { ok: true };
}
