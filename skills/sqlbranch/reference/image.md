# SqlBranch image commands

## sqlbranch image build

Build a sealed image from a backup file, a backup URL (https:// Azure Blob or s3://) or SQL scripts.

Prepare a reusable starting database for branches. A URL needs its secret in SQLBRANCH_AZURE_SAS_TOKEN or SQLBRANCH_S3_ACCESS_KEY_ID and SQLBRANCH_S3_SECRET_ACCESS_KEY, never in an argument.

```sh
sqlbranch image build shop --from-bak s3://172.17.0.5:9000/backups/shop.bak --s3-region eu-west-1
```

**Destroys** nothing

## sqlbranch image list

List available images and their provenance.

Choose the baseline required by the project.

```sh
sqlbranch image list --format json
```

**Destroys** nothing

## sqlbranch image export

Export a sealed image as one archive file that 'image import' can add to any SqlBranch pool.

Share or keep an image: move it to another machine, or save a nightly baseline. The image is not changed. An existing destination is refused unless --force is given. A URL (https:// Azure Blob or s3://) needs its secret in SQLBRANCH_AZURE_SAS_TOKEN or SQLBRANCH_S3_ACCESS_KEY_ID and SQLBRANCH_S3_SECRET_ACCESS_KEY, never in an argument.

```sh
sqlbranch image export shop --to shop.sbimg
```

**Destroys** nothing; with --force, the file it replaces, and only after the new archive is complete.

## sqlbranch image import

Import an image archive written by 'image export' as a new, sealed image.

Bring an image from another machine or a nightly baseline into this pool. The archive's engine must be as new as this engine (the same major version or older). An existing image is never replaced; use --as to pick another name. A URL (https:// Azure Blob or s3://) needs its secret in SQLBRANCH_AZURE_SAS_TOKEN or SQLBRANCH_S3_ACCESS_KEY_ID and SQLBRANCH_S3_SECRET_ACCESS_KEY, never in an argument.

```sh
sqlbranch image import shop.sbimg --as shop2
```

**Destroys** nothing; a failed import removes only what it made.

## sqlbranch image rm

Remove a sealed image.

Retire an image after checking its dependent branches.

```sh
sqlbranch image rm shop
```

**Destroys** The named image. With --force, existing branches lose their reset source.
