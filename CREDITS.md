# Crédits et sources d'inspiration

Queryeables reprend des **idées de conception** d'autres projets open source. Aucun code n'a été copié :
chaque fonctionnalité a été réécrite pour s'adapter au modèle de Queryeables (`IEntityStore`, entités ordinaires,
Dommel, EF Core, MagicOnion). Les licences sont citées pour information.

| Projet | Licence | Ce qui a été repris (idée) | Où dans Queryeables |
|---|---|---|---|
| [Datasync Community Toolkit](https://github.com/CommunityToolkit/Datasync) (.NET Foundation, ex-Azure Mobile Apps) | MIT | Date de modification (`UpdatedAt`) et suppression logique (`Deleted`) pour une synchronisation incrémentale ; jeton « depuis la dernière ligne reçue » ; taille de page maximale imposée par le serveur (`PageSize`, `MaxTop`) ; concurrence optimiste par version | `[LastModified]`, `[SoftDelete]`, `SyncConventionsStore`, `IEntityStore.SelectChangesAsync`, `EntityCache.SyncAsync`, paramètre `maxPageSize` d'`EntityHandler`, `ConcurrencyToken` |
| [Azure Mobile Apps — synchronisation hors ligne](https://azure.microsoft.com/en-gb/documentation/articles/app-service-mobile-offline-data-sync/) | Documentation Microsoft | Principe du jeton incrémental : on retient la date de la dernière ligne reçue, la synchronisation suivante ne demande que ce qui a changé depuis | `EntityCache.SyncAsync` |
| [ActualLab.Fusion](https://github.com/ActualLab/Fusion/wiki) | MIT | Regroupement des invalidations : une rafale de changements ne déclenche qu'une seule recomputation | Regroupement des notifications dans `EntityCache` |
| [ABP Framework — repositories](https://abp.io/docs/10.1/framework/architecture/domain-driven-design/repositories) | LGPL-3.0 | Idées d'API seulement : `GetAsync` qui lève une erreur quand rien ne correspond (à côté de `FindAsync`/`FirstOrDefaultAsync`), opérations groupées `InsertMany`/`UpdateMany`/`DeleteMany` | `EntityStoreExtensions.GetAsync`, `InsertManyAsync`, `UpdateManyAsync`, `DeleteManyAsync` |
| [Breeze](https://www.nuget.org/packages/Breeze.Persistence/7.3.1) | MIT | Enregistrer toutes les modifications d'un client en une seule transaction | Lots distants (`ExecuteBatchAsync`, transactions de `MagicOnionEntityStore`) |

## Considérés mais non repris

| Projet | Licence | Pourquoi |
|---|---|---|
| [Remote.Linq](https://github.com/6bee/Remote.Linq/wiki) | MIT | Exécute côté serveur des requêtes LINQ arbitraires envoyées par le client. Queryeables garde une liste blanche de conditions (`FilterCondition`) pour qu'un client ne puisse rien exécuter d'autre. |
| [Dotmim.Sync](https://dotmimsync.readthedocs.io/Overview.html) | MIT | Synchronise des bases entières par triggers et tables de suivi : autre niveau que les entités de Queryeables. Utile si l'on veut une copie complète de la base côté client. |
