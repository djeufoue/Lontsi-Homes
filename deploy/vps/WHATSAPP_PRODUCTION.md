# Mise à jour du VPS existant : WhatsApp

Pas de réinstallation du serveur, de nouvelle base, ni de copie de la base locale.
Les comptes, consentements et paiements de test locaux ne sont pas transférés.
Prévoir une courte interruption pendant la sauvegarde et la migration.

## 1. Secrets et configuration

Sur le VPS, conserver `.env.production` existant. Ne pas le remplacer par le fichier
example et ne pas modifier les clés SQL/JWT/Stripe/Azure qui fonctionnent déjà.

```bash
cd /opt/renthub
git pull --ff-only origin master
cd deploy/vps
openssl rand -hex 32
nano .env.production
chmod 600 .env.production
```

Le résultat OpenSSL est un **nouveau secret partagé indépendant de la clé API**.
Le copier dans `INFOBIP_WEBHOOK_SECRET`, puis dans le profil de notification Infobip.
Ne pas le publier, le committer ni le coller dans une capture d'écran.

Vérifier/ajouter ces entrées (remplacer les exemples) :

```dotenv
PORTAL_DOMAIN=lontsihomes.com
API_DOMAIN=api.lontsihomes.com
INFOBIP_BASE_URL=https://VOTRE-BASE-API-INFOBIP
INFOBIP_API_KEY=VOTRE_NOUVELLE_CLE_PROD
INFOBIP_WHATSAPP_SENDER=237691478387
INFOBIP_WEBHOOK_SECRET=LE_SECRET_OPENSSL
INFOBIP_SMS_SENDER=
REQUIRE_MAIN_PHONE_VERIFICATION=false
```

La clé Infobip doit autoriser `whatsapp:message:send`, avec accès au numéro approuvé.
Utiliser la base API indiquée dans le compte Infobip. Ne pas confondre cette adresse
avec `API_DOMAIN`, qui est l'API Lontsi Homes. Ne pas activer les SMS avant approbation.
Une nouvelle clé du même compte ne crée pas un nouveau numéro ni de nouveaux modèles.

Compose configure automatiquement `Infobip:ReceiptMediaBaseUrl=https://API_DOMAIN`.
Il n'y a **pas de ngrok en production**. Le nouveau volume `api_keys` conserve les
clés des liens PDF temporaires entre déploiements. Sauvegarder aussi ce volume et
les fichiers de secrets, séparément et dans un emplacement protégé.

### Boutons encore à confirmer

Les variables de texte sont raccordées. Les modèles avec boutons dynamiques ont
encore besoin de leur suffixe exact, selon l'URL approuvée chez Infobip :

```dotenv
WHATSAPP_BUTTON_LANDLORD_RECEIPT=
WHATSAPP_BUTTON_RENT_DUE_SOON=
WHATSAPP_BUTTON_RENT_DUE_TODAY=
WHATSAPP_BUTTON_RENT_OVERDUE=
WHATSAPP_BUTTON_TENANCY_ENDING=
```

`{verificationCode}` est disponible pour le reçu bailleur, `{tenancyId}` pour les
quatre autres. Ne pas renseigner au hasard : un suffixe vide désactive l'envoi de
ces notifications (`template_url_button_not_configured`). L'OTP et le reçu PDF du
locataire ne dépendent pas de ces boutons. Les anciens envois ignorés ne sont pas
rejoués automatiquement.

Les modèles FR/EN sont activés dans le code à la demande de l'utilisateur. Vérifier
leur statut réel avant production. `tenancy_request_received:fr` apparaissait en
**Marketing** sur une capture : faire corriger/confirmer sa catégorie, ou désactiver
ce modèle via `Infobip__Templates__tenancy_request_received__fr__Approved=false` dans
`.env.production`. Le consentement actuel n'inclut pas la publicité.

## 2. Profil webhook Infobip de production

Dans Developer Tools → Subscriptions Management :

- Créer un profil de notification « Lontsi Homes Production ».
- URL : `https://api.lontsihomes.com/api/webhooks/infobip/whatsapp`.
- Ajouter le header personnalisé **`X-Infobip-Webhook-Secret`** dont la valeur est
  exactement le secret généré à l'étape 1.
- Associer ce profil aux événements `INBOUND_MESSAGE`, `DELIVERY`, `SEEN` du bon
  numéro WhatsApp. Dans sa configuration entrante, appliquer l'abonnement de production.
- Ne plus faire pointer les événements de ce numéro vers ngrok une fois le basculement
  validé. Une même ligne WhatsApp ne sépare pas les destinataires de test et de production.

Le code vérifie un secret partagé, **pas une signature HMAC**, ni Basic/OAuth.
Ne pas sélectionner HMAC en supposant que ce champ est une clé de signature.
Le paramètre `?secret=...` est également accepté, mais le header est préférable
pour éviter le secret dans l'URL et les journaux. Ne pas journaliser ce header.

Documentation officielle :
- https://www.infobip.com/docs/subscriptions/manage
- https://www.infobip.com/docs/essentials/api-essentials/api-authorization

## 3. Déployer après la configuration

```bash
cd /opt/renthub/deploy/vps
bash deploy-update.sh
```

Le script valide Compose sans afficher les secrets, construit les images avant
l'interruption, arrête API/portail, réalise un backup SQL `COPY_ONLY` avec checksum,
exécute `RESTORE VERIFYONLY`, copie le backup dans `backups/`, puis démarre la version.
Il attend jusqu'à 5 minutes et vérifie la migration, les colonnes et index WhatsApp.
`RESTORE VERIFYONLY` vérifie le fichier ; cela ne remplace pas un exercice de restauration.
Copier le backup dans un stockage protégé hors VPS pour la protection contre sa perte.

Migration attendue : **20260911020456_AddInfobipMessagingAndWhatsAppConsent**.
EF applique les migrations manquantes au démarrage ; l'API valide ensuite les colonnes
avant d'ouvrir son port. La migration refuse les anciens numéros de plus de 16 caractères
au lieu de les tronquer. Le retrait d'un ancien réglage déjà absent ne bloque plus.
Les nouveaux consentements ne sont pas accordés automatiquement aux utilisateurs existants.

En cas d'erreur, arrêter la procédure et lire :

```bash
docker compose --env-file .env.production -f docker-compose.prod.yml ps
docker compose --env-file .env.production -f docker-compose.prod.yml logs --tail 150 api
```

Ne jamais utiliser `down -v`, supprimer les volumes ou insérer manuellement la ligne
de migration dans `__EFMigrationsHistory`. Une erreur après l'arrêt peut laisser le site
indisponible : examiner la cause avant de réessayer. Un retour arrière du code seul
ne rétablit pas les colonnes supprimées ; restaurer la sauvegarde demande un plan explicite.

## 4. Vérification fonctionnelle

- API `healthy` et message de validation des migrations dans les logs.
- Demande/validation d'un OTP pour un utilisateur de production consentant.
- Reçu PDF reçu et ouvrable, dans la langue des communications choisie.
- Webhook sans secret : HTTP 401 ; événements Infobip légitimes : HTTP 200.
- Retour des statuts et traitement de STOP. Ne pas tester STOP sur le compte d'un client.
- Tester les rappels seulement après configuration de leurs boutons.

Les tests locaux n'ont pas été exécutés contre la base réelle du VPS. Le script s'arrête
sur erreur ; il ne garantit pas l'absence de dérive dans une base qui n'a pas été inspectée.
